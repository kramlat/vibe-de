using Vde.Components;

namespace Vde.Shell;

public enum PlaceKind { Folder, Computer, Network, Trash, ShellFolder }

// An entry in the sidebar: a folder, or a place made by a shell folder (This Computer, Network, Trash, ...).
public sealed record Place(string Name, IReadOnlyList<string> IconNames, PlaceKind Kind, string? Path = null);

public static class Places
{
    // Home and the user's own folders that exist, then the shell folders registered for the sidebar. Without a
    // catalog the three standard places are listed but cannot be opened (Path is null), as before.
    public static IReadOnlyList<Place> Default(KnownFolders folders, ShellFolderCatalog? shell = null)
    {
        var places = new List<Place>();
        void Add(string name, string icon, KnownFolder folder)
        {
            var path = folders.Get(folder);
            if (Directory.Exists(path))
                places.Add(new Place(name, [icon, "folder"], PlaceKind.Folder, path));
        }

        if (shell is null)
        {
            Add("Home", "user-home", KnownFolder.Home);
            Add("Desktop", "user-desktop", KnownFolder.Desktop);
            Add("Documents", "folder-documents", KnownFolder.Documents);
            Add("Downloads", "folder-download", KnownFolder.Downloads);
            Add("Music", "folder-music", KnownFolder.Music);
            Add("Pictures", "folder-pictures", KnownFolder.Pictures);
            Add("Videos", "folder-videos", KnownFolder.Videos);
        }

        if (shell is null)
        {
            places.Add(new Place("This Computer", ["computer", "drive-harddisk"], PlaceKind.Computer));
            places.Add(new Place("Network", ["network-workgroup", "folder-remote"], PlaceKind.Network));
            places.Add(new Place("Trash", ["user-trash"], PlaceKind.Trash));
            return places;
        }

        // Everything else is whatever is registered for the sidebar: the user's folders (Home, Documents, Music...) are
        // shell folders standing for directories, so they are plain folder places; the rest open as shell folders.
        foreach (var info in shell.Namespace("Sidebar"))
        {
            IReadOnlyList<string> icons;
            try
            {
                var folder = shell.Open(info);
                if (folder is IFileSystemFolder { FileSystemPath: { } dir })
                {
                    if (Directory.Exists(dir)) places.Add(new Place(info.Name, folder.IconNames, PlaceKind.Folder, dir));
                    continue;
                }
                icons = folder.IconNames;
            }
            catch (ComponentLoadException) { icons = []; }   // untrusted or broken: still listed, with a plain icon, and it explains itself when opened
            places.Add(new Place(info.Name, icons.Count > 0 ? icons : ["folder"], PlaceKind.ShellFolder, info.Path));
        }
        return places;
    }
}
