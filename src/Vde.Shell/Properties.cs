using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vde.Components;
using Vibe.Registry;

namespace Vde.Shell;

// Everything the General and Details tabs show about one file or folder.
public sealed record FileProperties(
    string Name, string FullPath, string Folder, bool IsDirectory, bool IsSymlink, string? LinkTarget,
    long Size, DateTime ModifiedUtc, DateTime AccessedUtc, DateTime ChangedUtc,
    UnixFileMode Mode, uint Uid, uint Gid, string Owner, string Group, ulong Inode, uint HardLinks,
    string MimeType, string TypeDescription)
{
    public string ModeOctal => Convert.ToString((int)Mode & 0xFFF, 8).PadLeft(4, '0');
    public string ModeText => FileModeText(Mode);

    // "rwxr-xr-x"
    public static string FileModeText(UnixFileMode mode)
    {
        var text = new char[9];
        UnixFileMode[] bits = [UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute, UnixFileMode.GroupRead, UnixFileMode.GroupWrite,
            UnixFileMode.GroupExecute, UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute];
        for (int i = 0; i < 9; i++)
            text[i] = mode.HasFlag(bits[i]) ? "rwx"[i % 3] : '-';
        return new string(text);
    }

    // Reads one item. Links are described as themselves (not what they point at).
    public static FileProperties Read(string path, FileTypes? types = null, UserNames? names = null)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        bool isLink = info.LinkTarget is not null;
        bool isDirectory = Directory.Exists(path) && !isLink;
        if (!isLink && !isDirectory && !File.Exists(path))
            throw new FileNotFoundException("Nothing there.", path);

        var stat = Stat(path);
        names ??= UserNames.System;
        var mode = isLink ? stat.mode : File.GetUnixFileMode(path);
        var type = isDirectory ? new FileTypeInfo("inode/directory", "Folder", [])
            : isLink ? new FileTypeInfo("inode/symlink", "Link", [])
            : types?.Describe(path) ?? new FileTypeInfo("application/octet-stream", "File", []);

        return new FileProperties(
            Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : "/", path, Path.GetDirectoryName(path.TrimEnd('/')) ?? "/",
            isDirectory, isLink, info.LinkTarget,
            isDirectory || isLink ? 0 : info.Length,
            info.LastWriteTimeUtc, info.LastAccessTimeUtc, stat.changed ?? info.LastWriteTimeUtc,
            mode, stat.uid, stat.gid, names.User(stat.uid), names.Group(stat.gid), stat.inode, stat.links,
            type.MimeType, type.Description);
    }

    // uid, gid, inode, link count, ctime and the mode (for symlinks, where File.GetUnixFileMode would follow the link):
    // .NET has no call for these, and struct stat differs between architectures, so ask coreutils.
    private static (uint uid, uint gid, ulong inode, uint links, DateTime? changed, UnixFileMode mode) Stat(string path)
    {
        try
        {
            var info = new ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "%u|%g|%i|%h|%Z|%a", "--", path }) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var f = text.Split('|');
            if (p.ExitCode == 0 && f.Length == 6)
                return (uint.Parse(f[0], CultureInfo.InvariantCulture), uint.Parse(f[1], CultureInfo.InvariantCulture), ulong.Parse(f[2], CultureInfo.InvariantCulture),
                    uint.Parse(f[3], CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[4], CultureInfo.InvariantCulture)).UtcDateTime,
                    (UnixFileMode)Convert.ToInt32(f[5], 8));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or FormatException) { }
        return (0, 0, 0, 1, null, 0);
    }
}

// uid and gid to names, from /etc/passwd and /etc/group (a number stands in when there is no name).
public sealed class UserNames(string passwd, string group)
{
    public static UserNames System { get; } = new("/etc/passwd", "/etc/group");

    public string User(uint uid) => Lookup(passwd, uid) ?? uid.ToString(CultureInfo.InvariantCulture);
    public string Group(uint gid) => Lookup(group, gid) ?? gid.ToString(CultureInfo.InvariantCulture);

    private static string? Lookup(string file, uint id)
    {
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var f = line.Split(':');   // name:x:id:...
            if (f.Length >= 3 && f[0].Length > 0 && uint.TryParse(f[2], out var n) && n == id) return f[0];
        }
        return null;
    }
}

public sealed record FolderSize(long Bytes, int Files, int Folders, bool Complete);

// "Size" and "Contains: 12 files, 3 folders" for a folder, which needs a walk: so it can be cancelled, reports
// as it goes, and never follows symlinks (no loops, no counting what lies outside).
public static class FolderSizer
{
    public static FolderSize Measure(IEnumerable<string> paths, IProgress<FolderSize>? progress = null, CancellationToken cancel = default)
    {
        long bytes = 0; int files = 0, folders = 0;
        var stack = new Stack<string>(paths.Reverse());
        var tick = Environment.TickCount64;
        try
        {
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var path = stack.Pop();
                var info = new FileInfo(path);
                if (info.LinkTarget is not null) { files++; continue; }
                if (Directory.Exists(path))
                {
                    folders++;
                    try { foreach (var child in Directory.EnumerateFileSystemEntries(path)) stack.Push(child); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                else
                {
                    try { bytes += info.Length; files++; } catch (IOException) { }
                }
                if (Environment.TickCount64 - tick > 100) { progress?.Report(new FolderSize(bytes, files, folders, false)); tick = Environment.TickCount64; }
            }
        }
        catch (OperationCanceledException)
        {
            return new FolderSize(bytes, files, folders, false);
        }
        var result = new FolderSize(bytes, files, folders, true);
        progress?.Report(result);
        return result;
    }

    public static Task<FolderSize> MeasureAsync(IEnumerable<string> paths, IProgress<FolderSize>? progress = null, CancellationToken cancel = default)
        => Task.Run(() => Measure(paths, progress, cancel), cancel);
}

// What the Permissions tab edits: read, write and execute for owner, group and others, and the group.
public static class PermissionEditor
{
    public const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    // The new mode: bits in `set` turned on and bits in `clear` turned off, special bits (setuid, setgid, sticky) kept.
    public static UnixFileMode Change(UnixFileMode current, UnixFileMode set, UnixFileMode clear)
        => (current & ~(clear & Rwx)) | (set & Rwx);

    // Applies a mode to a path, and to everything below a folder when recursive. For recursive changes the
    // execute bit is only touched on folders and on files that already had it, so a file does not become a program
    // because its folder gained "execute" (needed to enter it). Returns how many items were changed.
    public static int Apply(string path, UnixFileMode set, UnixFileMode clear, bool recursive, CancellationToken cancel = default)
    {
        int changed = 0;
        void One(string p, bool isTop)
        {
            cancel.ThrowIfCancellationRequested();
            if (new FileInfo(p).LinkTarget is not null) return;   // a link's own mode means nothing; never follow it out
            bool isDir = Directory.Exists(p);
            var current = File.GetUnixFileMode(p);
            var s = set; var c = clear;
            if (!isTop && !isDir)
            {
                var exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                bool anyExec = (current & exec) != 0;
                s &= ~exec | (anyExec ? exec : 0);   // do not add execute to a plain file
            }
            var next = Change(current, s, c);
            if (next != current)
            {
                File.SetUnixFileMode(p, next);
                changed++;
            }
        }

        One(path, true);
        if (recursive && Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
        {
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var folder = stack.Pop();
                IEnumerable<string> children;
                try { children = Directory.EnumerateFileSystemEntries(folder).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var child in children)
                {
                    One(child, false);
                    if (Directory.Exists(child) && new FileInfo(child).LinkTarget is null) stack.Push(child);
                }
            }
        }
        return changed;
    }

    // Hands the item to a group (the owner may choose one they belong to). Changing the owner needs root.
    public static void SetGroup(string path, uint gid)
    {
        if (chown(path, uint.MaxValue, gid) != 0)   // -1 means "leave the owner alone"
            throw Marshal.GetLastPInvokeError() is 1 or 13 ? new UnauthorizedAccessException("You may only give this to a group you belong to.")
                : new IOException("The group could not be changed (error " + Marshal.GetLastPInvokeError() + ").");
    }

    public static void SetOwner(string path, uint uid)
    {
        if (chown(path, uid, uint.MaxValue) != 0)
            throw Marshal.GetLastPInvokeError() is 1 or 13 ? new UnauthorizedAccessException("Only an administrator can change the owner.")
                : new IOException("The owner could not be changed (error " + Marshal.GetLastPInvokeError() + ").");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}

// Which components add tabs to the Properties window for these items, found the way Explorer finds property sheet
// handlers: under HKCR\<class>\shellex\PropertySheetHandlers\<name>, default value = the handler's CLSID.
// The classes searched are "*" (every file), "Directory" (folders), the extension, and its ProgID.
public sealed class PropertyHandlers(ClassesRoot classes, RegistryIdentity? identity = null)
{
    public const string HandlersKey = @"shellex\PropertySheetHandlers";
    private readonly ComponentCatalog _catalog = new(classes, identity);

    public IReadOnlyList<ComponentRegistration> For(string path, bool isDirectory)
    {
        var found = new List<ComponentRegistration>();
        foreach (var cls in Classes(path, isDirectory))
        {
            var key = $@"{cls}\{HandlersKey}";
            if (!classes.KeyExists(key)) continue;
            foreach (var name in classes.GetSubKeyNames(key))
                if (classes.GetValue($@"{key}\{name}", "")?.Data is string clsid
                    && _catalog.Find(clsid) is { } registration
                    && !found.Any(f => f.Component.Clsid == registration.Component.Clsid))
                    found.Add(registration);
        }
        return found;
    }

    // Trusted handlers only; the others are returned separately so the sheet can say why a tab is missing.
    public (IReadOnlyList<ComponentRegistration> loadable, IReadOnlyList<ComponentRegistration> refused) Split(string path, bool isDirectory)
    {
        var all = For(path, isDirectory);
        return (all.Where(r => r.Trust.IsTrusted).ToList(), all.Where(r => !r.Trust.IsTrusted).ToList());
    }

    private IEnumerable<string> Classes(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            yield return "Directory";
            yield break;
        }
        yield return "*";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length > 1)
        {
            yield return ext;
            if (classes.GetValue(ext, "")?.Data is string progId && progId.Length > 0)
                yield return progId;
        }
    }
}
