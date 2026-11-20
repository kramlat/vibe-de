using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Vde.Shell.Controls;

// Rendered icons kept as small PNG files, so a program that starts does not parse and rasterize the same few
// dozen SVGs again (about 15 ms each, on the first frame) and does not walk the icon theme's directories to find
// them (about 100 ms the first time). A picture is named by what was asked for (theme, icon names, pixel size)
// and by a stamp of the installed themes: installing or updating a theme makes new files, and the old ones are
// simply never read again. Safe to delete at any time; safe for several programs to write at once.
internal sealed class IconDiskCache
{
    private const string Version = "v1";
    private readonly string _folder;
    private readonly string _stamp;

    public IconDiskCache(string stamp, string? root = null)
    {
        _stamp = stamp;
        var cache = root ?? (Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"));
        _folder = Path.Combine(cache, "vde", "icons", Version);
    }

    private string FileFor(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(_stamp + "\n" + key));
        return Path.Combine(_folder, Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + ".png");
    }

    public Bitmap? TryGet(string key)
    {
        try
        {
            var file = FileFor(key);
            return File.Exists(file) ? new Bitmap(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;   // a damaged file is just a miss; it is overwritten when the icon is rendered again
        }
    }

    // Best effort: the cache is an optimization, never a reason for a window to fail.
    public void Put(string key, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var file = FileFor(key);
            var temp = file + "." + Environment.ProcessId + ".tmp";
            bitmap.Save(temp, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            File.Move(temp, file, overwrite: true);   // atomic: a reader never sees half a picture
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { }
    }
}
