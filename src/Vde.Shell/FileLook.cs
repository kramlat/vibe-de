using System.Security.Cryptography;
using System.Text;
using Vde.Freedesktop;

namespace Vde.Shell;

// What a file looks like on the desktop, decided the way GNOME (Nautilus/GIO) and KDE (KIO/Dolphin) decide it:
//   1. a .desktop launcher is drawn with its own Icon= and named by its Name= (Hidden=true ones are not shown);
//   2. a file with a thumbnail in the freedesktop thumbnail cache (~/.cache/thumbnails, spec 0.8) shows that picture;
//   3. otherwise the file's MIME type decides (FileTypes: the registry first, then the MIME database).
// Folders keep their own .directory icon (FolderIcon). Nothing here makes thumbnails: it reads what a thumbnailer
// (GNOME, KDE, tumbler) already cached, as both desktops do before they draw anything themselves.
public static class FileLook
{
    public sealed record Look(string? Name, IReadOnlyList<string>? IconNames, bool Hidden);

    public static bool IsLauncher(string path) => path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase);

    // The look a .desktop file asks for, or null when it is not a readable launcher.
    public static Look? Launcher(string path, IReadOnlyList<string> locales)
    {
        if (!IsLauncher(path)) return null;
        DesktopEntry entry;
        try { entry = DesktopEntry.Load(path, Path.GetFileName(path), locales); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        var icons = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Icon))
        {
            var icon = entry.Icon!;
            // a relative path in Icon= is a theme name; an absolute one must exist
            if (!icon.StartsWith('/') || File.Exists(icon)) icons.Add(icon);
        }
        icons.Add(entry.Type == "Directory" ? "folder" : entry.Type == "Link" ? "text-html" : "application-x-executable");
        return new Look(string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name, icons, entry.Hidden);
    }

    // The cached thumbnail of a file, largest first, when one exists and is not older than the file.
    public static string? Thumbnail(string path, XdgDirectories? xdg = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant() + ".png";
            var root = Path.Combine((xdg ?? XdgDirectories.Default).CacheHome, "thumbnails");
            foreach (var size in new[] { "large", "x-large", "normal" })
            {
                var candidate = Path.Combine(root, size, name);
                if (File.Exists(candidate) && File.GetLastWriteTimeUtc(candidate) >= info.LastWriteTimeUtc)
                    return candidate;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException) { }
        return null;
    }
}
