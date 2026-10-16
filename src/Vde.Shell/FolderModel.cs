using System.Globalization;

namespace Vde.Shell;

public sealed record FileEntry(
    string Name, string FullPath, bool IsDirectory, bool IsSymlink, bool IsHidden, long Size, DateTime ModifiedUtc, string Extension)
{
    public string? LinkTarget { get; init; }
}

public enum SortKey { Name, Size, Modified, Type }

// Lists a folder the way a file manager shows it. Entries that vanish or cannot be read while listing are
// skipped, not fatal: a folder is never "all or nothing".
public static class FolderLister
{
    public static IReadOnlyList<FileEntry> List(string folder, bool showHidden = false)
    {
        var entries = new List<FileEntry>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            try
            {
                bool hidden = info.Name.StartsWith('.');
                if (hidden && !showHidden)
                    continue;
                var link = info.LinkTarget;
                bool isDirectory = info is DirectoryInfo || (link is not null && Directory.Exists(info.FullName));
                entries.Add(new FileEntry(info.Name, info.FullName, isDirectory, link is not null, hidden,
                    info is FileInfo file && link is null ? file.Length : 0, info.LastWriteTimeUtc,
                    isDirectory ? string.Empty : Path.GetExtension(info.Name).ToLowerInvariant()) { LinkTarget = link });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return entries;
    }

    // Folders first, then by the key; names compare "naturally" (file2 before file10) and ignoring case.
    public static IReadOnlyList<FileEntry> Sort(IEnumerable<FileEntry> entries, SortKey key = SortKey.Name, bool descending = false)
    {
        Comparison<FileEntry> by = key switch
        {
            SortKey.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortKey.Modified => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            SortKey.Type => (a, b) => NaturalCompare(a.Extension, b.Extension),
            _ => (a, b) => NaturalCompare(a.Name, b.Name),
        };
        var list = entries.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;                       // folders stay on top in either direction
            int result = by(a, b);
            if (result == 0 && key != SortKey.Name)
                result = NaturalCompare(a.Name, b.Name);
            return descending ? -result : result;
        });
        return list;
    }

    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLower(a[i], CultureInfo.InvariantCulture).CompareTo(char.ToLower(b[j], CultureInfo.InvariantCulture));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

// Back, forward and up, like the buttons in a file manager window.
public sealed class NavigationHistory
{
    private readonly List<string> _entries = new();
    private int _index = -1;

    public NavigationHistory(string start) => Navigate(start);

    public string Current => _entries[_index];
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index < _entries.Count - 1;
    public string? Parent => Path.GetDirectoryName(Current.TrimEnd('/')) is { Length: > 0 } p ? p : Current == "/" ? null : "/";
    public bool CanGoUp => Parent is not null;

    // Going somewhere new drops the forward list, as a browser does. Going to where you already are changes nothing.
    public void Navigate(string path)
    {
        path = Normalize(path);
        if (_index >= 0 && _entries[_index] == path)
            return;
        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(path);
        _index++;
    }

    public string Back() => CanGoBack ? _entries[--_index] : Current;
    public string Forward() => CanGoForward ? _entries[++_index] : Current;
    public string Up()
    {
        if (Parent is { } parent)
            Navigate(parent);
        return Current;
    }

    private static string Normalize(string path)
        => path.Length > 1 ? Path.GetFullPath(path).TrimEnd('/') is { Length: > 0 } p ? p : "/" : path;
}

// The text in the address bar: breadcrumb segments, and turning what the user typed back into a path.
public static class AddressPath
{
    public sealed record Crumb(string Name, string Path);

    // "/home/deck/Music" -> / , home, deck, Music, each with the path that clicking it goes to.
    public static IReadOnlyList<Crumb> Crumbs(string path, string? home = null)
    {
        var crumbs = new List<Crumb> { new("/", "/") };
        var built = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built += "/" + part;
            crumbs.Add(new Crumb(built == home ? "Home" : part, built));
        }
        return crumbs;
    }

    // "~", "~/x", "file:///x%20y", a relative path (against current) or an absolute one. Null when it is not a place.
    public static string? Parse(string text, string current, string home)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (text.StartsWith("file://", StringComparison.Ordinal))
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.LocalPath : null;
        if (text == "~") return home;
        if (text.StartsWith("~/", StringComparison.Ordinal)) return Path.GetFullPath(Path.Combine(home, text[2..]));
        if (text.Contains("://", StringComparison.Ordinal)) return null;   // other schemes are not folders
        return Path.GetFullPath(text, current);
    }
}

// Tells the caller when a folder's contents changed. Bursts (a copy writing many files) become one callback.
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;

    public FolderWatcher(string folder, Action changed, TimeSpan? delay = null)
    {
        var wait = delay ?? TimeSpan.FromMilliseconds(100);
        _debounce = new Timer(_ => changed(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler handler = (_, _) => _debounce.Change(wait, Timeout.InfiniteTimeSpan);
        _watcher.Created += handler; _watcher.Deleted += handler; _watcher.Changed += handler;
        _watcher.Renamed += (_, _) => _debounce.Change(wait, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
