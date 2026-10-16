using System.Text;

namespace Vde.Shell;

public enum TransferKind { Copy, Cut }

// Files on the clipboard or being dragged, in the formats the other desktops understand, so Copy in one
// file manager pastes in another:
//   text/uri-list                     file:// URIs, CRLF separated
//   x-special/gnome-copied-files      "copy" or "cut", then the URIs, newline separated (GNOME, Cinnamon, ...)
//   application/x-kde-cutselection    "1" when the files were cut (KDE)
public sealed record FileTransfer(TransferKind Kind, IReadOnlyList<string> Paths)
{
    public const string UriList = "text/uri-list";
    public const string GnomeCopiedFiles = "x-special/gnome-copied-files";
    public const string KdeCutSelection = "application/x-kde-cutselection";

    public IReadOnlyDictionary<string, byte[]> ToFormats()
    {
        var uris = Paths.Select(p => new Uri(p).AbsoluteUri).ToList();
        return new Dictionary<string, byte[]>
        {
            [UriList] = Encoding.UTF8.GetBytes(string.Concat(uris.Select(u => u + "\r\n"))),
            [GnomeCopiedFiles] = Encoding.UTF8.GetBytes((Kind == TransferKind.Cut ? "cut" : "copy") + "\n" + string.Join('\n', uris)),
            [KdeCutSelection] = Encoding.UTF8.GetBytes(Kind == TransferKind.Cut ? "1" : "0"),
        };
    }

    // Reads whatever formats the clipboard offered. Null when there are no usable local files in it.
    public static FileTransfer? FromFormats(IReadOnlyDictionary<string, byte[]> formats)
    {
        var kind = TransferKind.Copy;
        IEnumerable<string> lines;
        if (formats.TryGetValue(GnomeCopiedFiles, out var gnome))
        {
            var parts = Encoding.UTF8.GetString(gnome).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts[0] is "cut" or "copy")
            {
                kind = parts[0] == "cut" ? TransferKind.Cut : TransferKind.Copy;
                lines = parts.Skip(1);
            }
            else lines = parts;
        }
        else if (formats.TryGetValue(UriList, out var list))
            lines = Encoding.UTF8.GetString(list).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#'));
        else
            return null;

        if (formats.TryGetValue(KdeCutSelection, out var kde) && Encoding.UTF8.GetString(kde).Trim() == "1")
            kind = TransferKind.Cut;

        var paths = new List<string>();
        foreach (var line in lines.Select(l => l.Trim()))
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile && (uri.Host.Length == 0 || uri.Host == "localhost"))
                paths.Add(uri.LocalPath);   // only local files: a file://otherhost/ URI is not ours to read
        return paths.Count == 0 ? null : new FileTransfer(kind, paths);
    }
}

public enum DropEffect { None, Copy, Move, Link }

// What a drop means: where it may go, and whether it copies or moves. The usual rules: Ctrl copies, Shift moves,
// Ctrl+Shift would link; otherwise within one filesystem it moves and across filesystems it copies.
public static class DropRules
{
    public static DropEffect Decide(IReadOnlyList<string> sources, string targetFolder, bool ctrl, bool shift, Func<string, string, bool>? sameVolume = null)
    {
        if (!Directory.Exists(targetFolder) || sources.Count == 0)
            return DropEffect.None;
        var target = Path.GetFullPath(targetFolder).TrimEnd('/');

        foreach (var source in sources)
        {
            var s = Path.GetFullPath(source).TrimEnd('/');
            if (Directory.Exists(s) && (target == s || target.StartsWith(s + "/", StringComparison.Ordinal)))
                return DropEffect.None;   // a folder cannot go into itself or something below it
        }
        if (ctrl && shift) return DropEffect.Link;
        if (ctrl) return DropEffect.Copy;
        if (shift) return DropEffect.Move;

        // Dropping where it already is would do nothing.
        if (sources.All(s => string.Equals(Path.GetDirectoryName(Path.GetFullPath(s).TrimEnd('/')), target, StringComparison.Ordinal)))
            return DropEffect.None;
        sameVolume ??= SameVolume;
        return sources.All(s => sameVolume(s, targetFolder)) ? DropEffect.Move : DropEffect.Copy;
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(a) ?? "/").Name == new DriveInfo(Path.GetPathRoot(b) ?? "/").Name
                && StatDevice(a) == StatDevice(b);
        }
        catch (Exception e) when (e is IOException or ArgumentException) { return false; }
    }

    private static string? StatDevice(string path)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "%d", "--", path }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return null; }
    }
}

// The things you do with files in a folder window or on the desktop, with the rules in one place so both
// behave the same: paste, drop, rename, new folder, delete (to the trash unless told otherwise).
public sealed class FileActions(Trash trash)
{
    public IReadOnlyList<string> Paste(FileTransfer transfer, string folder, FileOperationOptions? options = null)
        => Apply(transfer.Kind == TransferKind.Cut ? DropEffect.Move : DropEffect.Copy, transfer.Paths, folder, options);

    public IReadOnlyList<string> Drop(IReadOnlyList<string> sources, string folder, DropEffect effect, FileOperationOptions? options = null)
        => Apply(effect, sources, folder, options);

    private static IReadOnlyList<string> Apply(DropEffect effect, IReadOnlyList<string> sources, string folder, FileOperationOptions? options)
    {
        var existing = sources.Where(s => File.Exists(s) || Directory.Exists(s) || new FileInfo(s).LinkTarget is not null).ToList();   // something may have been deleted meanwhile
        return effect switch
        {
            DropEffect.Copy => FileOperations.Copy(existing, folder, options),
            DropEffect.Move => FileOperations.Move(existing, folder, options),
            DropEffect.Link => Link(existing, folder),
            _ => [],
        };
    }

    private static IReadOnlyList<string> Link(IReadOnlyList<string> sources, string folder)
    {
        var made = new List<string>();
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source.TrimEnd('/'));
            var link = Path.Combine(folder, name + " - link");
            for (int i = 2; File.Exists(link) || Directory.Exists(link) || new FileInfo(link).LinkTarget is not null; i++)
                link = Path.Combine(folder, $"{name} - link {i}");
            File.CreateSymbolicLink(link, Path.GetFullPath(source));
            made.Add(link);
        }
        return made;
    }

    public string Rename(string path, string newName) => FileOperations.Rename(path, newName);

    public string NewFolder(string parent) => FileOperations.CreateFolder(parent);

    // To the trash, so it can be brought back. permanent skips the trash.
    public void Delete(IEnumerable<string> paths, bool permanent = false)
    {
        foreach (var path in paths.ToList())
            if (permanent) FileOperations.Delete([path]);
            else trash.Send(path);
    }
}
