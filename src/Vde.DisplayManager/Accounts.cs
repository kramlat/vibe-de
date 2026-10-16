using Vde.Freedesktop;

namespace Vde.DisplayManager;

public sealed record LoginUser(string Name, int Uid, int Gid, string Home, string Shell, string FullName)
{
    // A face picture, if the user has one: ~/.face, or what AccountsService keeps.
    public string? Icon(string accountsServiceDir = "/var/lib/AccountsService/icons")
    {
        foreach (var candidate in new[] { Path.Combine(Home, ".face"), Path.Combine(Home, ".face.icon"), Path.Combine(accountsServiceDir, Name) })
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}

// The people who can log in here: real accounts (uid from 1000 up) with a shell. Read from /etc/passwd; accounts
// from other directory services (LDAP, systemd-homed) are found by name when someone types it in.
public static class UserDirectory
{
    public static IReadOnlyList<LoginUser> Load(string passwdPath = "/etc/passwd", int minUid = 1000, int maxUid = 60000)
    {
        var users = new List<LoginUser>();
        if (!File.Exists(passwdPath)) return users;
        foreach (var line in File.ReadLines(passwdPath))
            if (Parse(line) is { } user && user.Uid >= minUid && user.Uid < maxUid && CanLogIn(user.Shell))
                users.Add(user);
        return users.OrderBy(u => u.FullName.Length > 0 ? u.FullName : u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static LoginUser? Find(string name, string passwdPath = "/etc/passwd")
    {
        if (!File.Exists(passwdPath)) return null;
        return File.ReadLines(passwdPath).Select(Parse).FirstOrDefault(u => u is not null && u.Name == name);
    }

    // name:password:uid:gid:gecos:home:shell
    public static LoginUser? Parse(string line)
    {
        if (line.Length == 0 || line[0] == '#') return null;
        var f = line.Split(':');
        if (f.Length < 7 || !int.TryParse(f[2], out var uid) || !int.TryParse(f[3], out var gid) || f[0].Length == 0) return null;
        return new LoginUser(f[0], uid, gid, f[5], f[6], f[4].Split(',')[0]);
    }

    public static bool CanLogIn(string shell)
        => shell.Length > 0 && !shell.EndsWith("/nologin", StringComparison.Ordinal) && !shell.EndsWith("/false", StringComparison.Ordinal);
}

public enum SessionKind { Wayland, X11 }

public sealed record SessionEntry(string Id, string Name, string Comment, IReadOnlyList<string> Command, SessionKind Kind, string? DesktopNames);

// The desktops that can be chosen at login: the .desktop files in wayland-sessions/ (and xsessions/, listed but
// not yet startable: vdedm has no X server launcher yet).
public static class SessionDirectory
{
    public const string DefaultId = "vde";

    public static IReadOnlyList<SessionEntry> Find(IEnumerable<string> dataDirs, Func<string, bool>? exists = null)
    {
        exists ??= _ => true;
        var found = new Dictionary<string, SessionEntry>();
        foreach (var dir in dataDirs)
            foreach (var (folder, kind) in new[] { ("wayland-sessions", SessionKind.Wayland), ("xsessions", SessionKind.X11) })
            {
                var path = Path.Combine(dir, folder);
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*.desktop").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (found.ContainsKey(id)) continue;   // an earlier data directory wins, as everywhere in XDG
                    DesktopEntry entry;
                    try { entry = DesktopEntry.Load(file, id, []); }
                    catch (Exception e) when (e is IOException or InvalidDataException or FormatException) { continue; }
                    if (entry.Hidden || entry.NoDisplay || entry.Exec is null) continue;
                    if (entry.TryExec is { Length: > 0 } tryExec && !exists(tryExec)) continue;
                    var command = ExecLine.Tokenize(entry.Exec);
                    if (command.Count == 0) continue;
                    found[id] = new SessionEntry(id, entry.Name, entry.Comment ?? "", command, kind, DesktopNames(file));
                }
            }
        return found.Values.OrderBy(s => s.Id == DefaultId ? 0 : 1).ThenBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The `DesktopNames=` key (becomes XDG_CURRENT_DESKTOP, colon separated): not part of DesktopEntry, so read it here.
    private static string? DesktopNames(string file)
        => File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("DesktopNames=", StringComparison.Ordinal))?[13..].Replace(';', ':').TrimEnd(':');

    public static SessionEntry? Choose(IReadOnlyList<SessionEntry> sessions, string? wantedId)
        => sessions.FirstOrDefault(s => s.Id == wantedId && s.Kind == SessionKind.Wayland)
           ?? sessions.FirstOrDefault(s => s.Id == DefaultId && s.Kind == SessionKind.Wayland)
           ?? sessions.FirstOrDefault(s => s.Kind == SessionKind.Wayland);
}
