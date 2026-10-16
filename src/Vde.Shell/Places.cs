namespace Vde.Shell;

public enum PlaceKind { Folder, Computer, Network, Trash }

// An entry in the sidebar: a folder, or one of the special places (This Computer, Network, Trash).
public sealed record Place(string Name, IReadOnlyList<string> IconNames, PlaceKind Kind, string? Path = null);

public static class Places
{
    // Home and the user's own folders that exist, then Computer, Network and Trash.
    public static IReadOnlyList<Place> Default(KnownFolders folders)
    {
        var places = new List<Place>();
        void Add(string name, string icon, KnownFolder folder)
        {
            var path = folders.Get(folder);
            if (Directory.Exists(path))
                places.Add(new Place(name, [icon, "folder"], PlaceKind.Folder, path));
        }

        Add("Home", "user-home", KnownFolder.Home);
        Add("Desktop", "user-desktop", KnownFolder.Desktop);
        Add("Documents", "folder-documents", KnownFolder.Documents);
        Add("Downloads", "folder-download", KnownFolder.Downloads);
        Add("Music", "folder-music", KnownFolder.Music);
        Add("Pictures", "folder-pictures", KnownFolder.Pictures);
        Add("Videos", "folder-videos", KnownFolder.Videos);
        places.Add(new Place("This Computer", ["computer", "drive-harddisk"], PlaceKind.Computer));
        places.Add(new Place("Network", ["network-workgroup", "folder-remote"], PlaceKind.Network));
        places.Add(new Place("Trash", ["user-trash"], PlaceKind.Trash));
        return places;
    }
}
