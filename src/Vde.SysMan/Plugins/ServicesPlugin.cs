using Vibe.Registry;

namespace Vde.SysMan;

// Services\<name>: the system's services as Windows lists them, taken from systemd's unit files (no process is
// started and systemd is not asked, so this works with any service manager that leaves unit files around).
//   Start: 2 automatic (enabled), 3 manual, 4 disabled (masked)   Type: 16 (a program of its own)
//   DisplayName and Description from the unit's Description, ImagePath from ExecStart, ObjectName from User.
public sealed class ServicesPlugin : ISystemPlugin
{
    // highest priority first, as systemd reads them: an admin's unit (or a mask, a link to /dev/null) in etc beats the packaged one
    private static readonly string[] UnitDirs = ["etc/systemd/system", "usr/lib/systemd/system", "lib/systemd/system"];

    public string Name => "Services";
    public bool IsAvailable(string root) => UnitDirs.Any(d => Directory.Exists(Path.Combine(root, d)));
    public IEnumerable<string> WatchedFiles(string root) => [];
    public IEnumerable<string> WatchedDirectories(string root) => [Path.Combine(root, "etc/systemd/system")];

    public void Fill(SystemContext c)
    {
        var enabled = EnabledUnits(c.Root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in UnitDirs.Select(d => Path.Combine(c.Root, d)).Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*.service").OrderBy(f => f, StringComparer.Ordinal))
            {
                var unit = Path.GetFileName(file);
                if (unit.Contains("@.", StringComparison.Ordinal) || !seen.Add(unit))
                    continue;   // templates are not services until instantiated; the first directory that has a unit wins
                if (new FileInfo(file).LinkTarget is { } link && link.EndsWith("/dev/null", StringComparison.Ordinal))
                { Write(c, unit, null, null, null, 4); continue; }   // masked
                var (description, exec, user) = Parse(file);
                Write(c, unit, description, exec, user, enabled.Contains(unit) ? 2 : 3);
            }
    }

    private static void Write(SystemContext c, string unit, string? description, string? exec, string? user, int start)
    {
        var name = unit[..^".service".Length];
        var key = @"Services\" + name;
        c.ControlSetKey(key);
        c.ControlSetText(key, "DisplayName", description ?? name);
        c.ControlSetText(key, "Description", description);
        c.ControlSetText(key, "ImagePath", exec);
        c.ControlSetNumber(key, "Start", start);
        c.ControlSetNumber(key, "Type", 0x10);
        c.ControlSetNumber(key, "ErrorControl", 1);
        c.ControlSetText(key, "ObjectName", user ?? "LocalSystem");
    }

    // Units wanted by a target: a symlink in etc/systemd/system/<target>.wants/ (that is what "enable" makes).
    private static HashSet<string> EnabledUnits(string root)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var etc = Path.Combine(root, "etc/systemd/system");
        if (Directory.Exists(etc))
            foreach (var wants in Directory.EnumerateDirectories(etc, "*.wants"))
                foreach (var link in Directory.EnumerateFileSystemEntries(wants, "*.service"))
                    enabled.Add(Path.GetFileName(link));
        return enabled;
    }

    private static (string? Description, string? Exec, string? User) Parse(string file)
    {
        string? description = null, exec = null, user = null, group = "";
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { group = line; continue; }
                var eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] is '#' or ';' || eq < 1)
                    continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (group == "[Unit]" && key == "Description") description ??= value;
                else if (group == "[Service]" && key == "ExecStart") exec ??= value.TrimStart('-', '@', '+', '!', ':');
                else if (group == "[Service]" && key == "User") user ??= value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (description, exec, user);
    }
}
