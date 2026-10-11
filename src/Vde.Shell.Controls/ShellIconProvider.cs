using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Vde.Freedesktop;

namespace Vde.Shell.Controls;

// Turns freedesktop icon names into images, using the current icon theme. Breeze is mostly SVG.
// Images are cached by file; use from the UI thread.
public sealed class ShellIconProvider
{
    private static ShellIconProvider? _default;

    public static ShellIconProvider Default
    {
        get => _default ??= new ShellIconProvider();
        set => _default = value;
    }

    private readonly IconThemeLookup _lookup;
    private readonly Dictionary<string, IImage?> _cache = new(StringComparer.Ordinal);
    private string _theme;

    public ShellIconProvider(IconThemeLookup? lookup = null, string theme = IconThemeLookup.DefaultTheme)
    {
        _lookup = lookup ?? new IconThemeLookup();
        _theme = theme;
    }

    public string Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Controls listen to this and reload.
    public event EventHandler? ThemeChanged;

    // The first name the theme has; a generic icon when none is found, so the UI never shows a hole.
    // size is in logical pixels, scale is the display scale.
    public IImage? Load(IEnumerable<string> names, int size, double scale = 1)
    {
        int factor = Math.Max(1, (int)Math.Ceiling(scale));
        foreach (var name in names.Append("application-x-generic"))
            if (_lookup.Find(name, size, factor, _theme) is { } file)
                return ImageFor(file.Path);
        return null;
    }

    private IImage? ImageFor(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        IImage? image = null;
        try
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var source = SvgSource.Load(path, null);
                image = source is null ? null : new SvgImage { Source = source };
            }
            else
            {
                image = new Bitmap(path);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A broken icon file (bad XML, truncated PNG, unreadable) must never break the window that shows it.
        }
        _cache[path] = image;
        return image;
    }
}
