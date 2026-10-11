using System.Globalization;
using System.Text;
using Vde.Freedesktop;

namespace Vde.Shell;

public sealed record TrashItem(string StoredName, string OriginalPath, DateTime DeletedLocal, bool IsDirectory);

// The freedesktop.org Trash specification, home trash only: $XDG_DATA_HOME/Trash/{files,info}.
// Files on other mounts are copied into the home trash rather than getting a per-volume .Trash-$uid.
public sealed class Trash
{
    private const string InfoGroup = "Trash Info";

    private readonly string _filesDir;
    private readonly string _infoDir;

    public Trash(XdgDirectories? xdg = null)
    {
        var root = Path.Combine((xdg ?? XdgDirectories.Default).DataHome, "Trash");
        _filesDir = Path.Combine(root, "files");
        _infoDir = Path.Combine(root, "info");
    }

    public TrashItem Send(string path)
    {
        path = Path.GetFullPath(path);
        bool isDirectory = Directory.Exists(path) && !IsSymlink(path);
        if (!isDirectory && !File.Exists(path) && !IsSymlink(path))
            throw new FileNotFoundException("Nothing to trash.", path);

        Directory.CreateDirectory(_filesDir);
        Directory.CreateDirectory(_infoDir);

        // The .trashinfo file is created exclusively first: that reserves the stored name atomically.
        var (name, infoPath) = ReserveName(Path.GetFileName(path), path);
        var stored = Path.Combine(_filesDir, name);
        try
        {
            Move(path, stored, isDirectory);
        }
        catch
        {
            File.Delete(infoPath);
            throw;
        }
        return new TrashItem(name, path, DateTime.Now, isDirectory);
    }

    public IReadOnlyList<TrashItem> List()
    {
        if (!Directory.Exists(_infoDir))
            return [];

        var items = new List<TrashItem>();
        foreach (var info in Directory.EnumerateFiles(_infoDir, "*.trashinfo"))
        {
            var name = Path.GetFileNameWithoutExtension(info);
            var stored = Path.Combine(_filesDir, name);
            if (!Directory.Exists(stored) && !File.Exists(stored) && !IsSymlink(stored))
                continue;   // orphaned info file

            var ini = IniFile.Load(info);
            var original = ini.GetRaw(InfoGroup, "Path");
            if (original is null)
                continue;
            DateTime.TryParse(ini.GetRaw(InfoGroup, "DeletionDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when);
            items.Add(new TrashItem(name, DecodePath(original), when, Directory.Exists(stored) && !IsSymlink(stored)));
        }
        return items.OrderByDescending(i => i.DeletedLocal).ToList();
    }

    // Puts the item back (or at destination). Never overwrites: an existing file is an IOException.
    public string Restore(TrashItem item, string? destination = null)
    {
        destination = Path.GetFullPath(destination ?? item.OriginalPath);
        if (File.Exists(destination) || Directory.Exists(destination) || IsSymlink(destination))
            throw new IOException($"'{destination}' already exists.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Move(Path.Combine(_filesDir, item.StoredName), destination, item.IsDirectory);
        File.Delete(InfoPath(item.StoredName));
        return destination;
    }

    public void Delete(TrashItem item)
    {
        var stored = Path.Combine(_filesDir, item.StoredName);
        if (item.IsDirectory)
            Directory.Delete(stored, recursive: true);
        else
            File.Delete(stored);
        File.Delete(InfoPath(item.StoredName));
    }

    public void Empty()
    {
        foreach (var item in List())
            Delete(item);

        // Anything left has no info file (or no data): clear it too.
        foreach (var dir in new[] { _filesDir, _infoDir })
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    File.Delete(file);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Directory.Delete(sub, recursive: true);
            }
    }

    public bool IsEmpty => !Directory.Exists(_infoDir) || !Directory.EnumerateFileSystemEntries(_infoDir).Any();

    private string InfoPath(string storedName) => Path.Combine(_infoDir, storedName + ".trashinfo");

    private (string name, string infoPath) ReserveName(string baseName, string originalPath)
    {
        var content = $"[{InfoGroup}]\nPath={EncodePath(originalPath)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var ext = Path.GetExtension(baseName);

        for (int attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{stem}.{attempt}{ext}";
            var info = InfoPath(name);
            if (File.Exists(Path.Combine(_filesDir, name)) || Directory.Exists(Path.Combine(_filesDir, name)))
                continue;
            try
            {
                using var stream = new FileStream(info, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.UTF8.GetBytes(content));
                return (name, info);
            }
            catch (IOException) when (File.Exists(info))
            {
                // someone else holds this name: try the next
            }
        }
    }

    private static void Move(string source, string destination, bool isDirectory)
    {
        try
        {
            if (isDirectory)
                Directory.Move(source, destination);
            else
                File.Move(source, destination);
        }
        catch (IOException) when (File.Exists(source) || Directory.Exists(source))
        {
            // Different filesystem: copy, then remove the original.
            FileOperations.CopyTree(source, destination, CancellationToken.None, null);
            if (isDirectory)
                Directory.Delete(source, recursive: true);
            else
                File.Delete(source);
        }
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Slashes stay, everything unusual is %XX, as the spec requires.
    internal static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    internal static string DecodePath(string encoded) => Uri.UnescapeDataString(encoded);
}
