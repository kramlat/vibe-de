using Vde.Components;

namespace Vde.Shell;

// What a location shows: its title and icon, and its entries (a directory's files, or a shell folder's items, which
// carry their ShellItem in FileEntry.Tag).
public sealed record VfsListing(string Title, IReadOnlyList<string> IconNames, IReadOnlyList<FileEntry> Entries, IShellFolder? Folder);

public enum VfsOpenKind { None, Enter, OpenFile, RunAction }

// What opening an item means: go into Path, open the file at Path, run Action in the shell folder, or nothing.
public sealed record VfsOpen(VfsOpenKind Kind, string? Path = null, string? Action = null);

// The one place that knows locations are either directories or shell folders ("shell:Computer", "::{clsid}"), so
// every consumer (file manager, file dialogs, later ShellExecute, file operations, the terminal) resolves them the
// same way instead of special-casing shell folders itself. Locations are strings: a directory path or a shell path.
public sealed class ShellVfs(ShellFolderCatalog catalog)
{
    public ShellFolderCatalog Catalog { get; } = catalog;

    // The VFS of this process, set by the host that builds the shell folder catalog. Anything that touches files
    // passes its locations through Resolve, so "shell:Music" or "shell:Computer/d" work wherever a path does.
    public static ShellVfs? Default { get; set; }

    // A location as the real path to use: a directory path stays as it is; a shell folder that stands for a directory
    // becomes that directory; an item of a shell folder (a drive in This PC) becomes its path. Throws IOException for a
    // place that holds no files (Network itself, Control Panel). Without a VFS, shell paths are left alone.
    public static string Resolve(string location)
    {
        if (!IsVirtual(location) || Default is not { } vfs) return location;
        return vfs.ToPath(location) ?? throw new IOException($"\"{location}\" is not a place that holds files.");
    }

    public static IEnumerable<string> Resolve(IEnumerable<string> locations) => locations.Select(Resolve).ToList();

    public string? ToPath(string location)
    {
        if (!IsVirtual(location)) return location;
        var directory = Normalize(location);
        if (!IsVirtual(directory)) return directory;
        // shell:Folder/itemId or ::{clsid}/itemId: the item's own path
        var start = location.StartsWith("::", StringComparison.Ordinal) ? 0 : "shell:".Length;
        var slash = location.IndexOf('/', start);
        if (slash < 0) return null;
        try { return List(location[..slash]).Entries.Select(e => e.Tag).OfType<ShellItem>().FirstOrDefault(i => i.Id == location[(slash + 1)..])?.FileSystemPath; }
        catch (Exception e) when (e is ComponentLoadException or InvalidOperationException or IOException) { return null; }
    }

    public static bool IsVirtual(string location) => AddressPath.IsShellPath(location);

    // The location to actually use: a shell folder that stands for a directory (shell:Music) is that directory, as in
    // Windows, so the real file operations, watching and sorting apply. Anything else stays as it is.
    public string Normalize(string location)
        => IsVirtual(location) && Catalog.DirectoryOf(location) is { } directory ? directory : location;

    public bool Exists(string location)
        => IsVirtual(location) ? Catalog.Resolve(location) is not null : Directory.Exists(location);

    // The items of a shell folder. Throws ComponentLoadException / InvalidOperationException when it is unknown, not
    // trusted or fails to load; the caller shows the message.
    public VfsListing List(string location)
    {
        var info = Catalog.Resolve(location) ?? throw new InvalidOperationException("This place is not available.");
        var folder = Catalog.Open(info);
        var entries = folder.GetItems().Select(i => new FileEntry(i.Name, location + "/" + i.Id, i.IsFolder, false, false, i.Size ?? 0, i.ModifiedUtc ?? default, "")
            { IconNames = i.IconNames, TypeText = i.TypeText, Detail = i.Detail, Tag = i, Group = i.Group, Usage = i.Usage }).ToList();
        return new VfsListing(folder.DisplayName, folder.IconNames, entries, folder);
    }

    public static VfsOpen Open(ShellItem item)
    {
        if (item.ChildFolder is { } child) return new(VfsOpenKind.Enter, child);
        if (item.FileSystemPath is { } path)
            return Directory.Exists(path) ? new(VfsOpenKind.Enter, path) : File.Exists(path) ? new(VfsOpenKind.OpenFile, path) : new(VfsOpenKind.None);
        return item.DefaultAction is { } action ? new(VfsOpenKind.RunAction, Action: action) : new(VfsOpenKind.None);
    }
}
