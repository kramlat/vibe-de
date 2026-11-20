using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Vde.Freedesktop;

namespace Vde.Shell.Controls;

// Turns freedesktop icon names into images, using the current icon theme. Breeze is mostly SVG.
// Three levels keep a window from waiting on icons: pictures already made in this process, the on-disk cache
// of rendered icons (see IconDiskCache), and only then the theme lookup and rasterizing, which can also be
// done on a worker thread (LoadAsync) so the UI thread never stalls on them.
public sealed class ShellIconProvider
{
    private static ShellIconProvider? _default;

    public static ShellIconProvider Default
    {
        get => _default ??= new ShellIconProvider();
        set => _default = value;
    }

    // True in the shell's programs (RenderSetup): ShellIcon then shows the icon when a worker thread has made it
    // instead of making it on the UI thread. Tests and tools leave it off and get the picture at once.
    public static bool Async { get; set; }

    private readonly IconThemeLookup _lookup;
    private readonly ConcurrentDictionary<string, IImage?> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IImage?> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<IImage?>> _pending = new(StringComparer.Ordinal);
    private readonly object _lookupLock = new();   // IconThemeLookup keeps unsynchronized caches
    private readonly bool _useDisk;
    private IconDiskCache? _disk;
    private string _theme;

    // useDiskCache: off for tests that give their own lookup (their themes are throwaway folders).
    public ShellIconProvider(IconThemeLookup? lookup = null, string theme = IconThemeLookup.DefaultTheme, bool? useDiskCache = null)
    {
        _lookup = lookup ?? new IconThemeLookup();
        _theme = theme;
        _useDisk = useDiskCache ?? lookup is null;   // the real theme and the real cache directory, or neither
    }

    public string Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            _memory.Clear();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Controls listen to this and reload.
    public event EventHandler? ThemeChanged;

    private static string Key(string theme, IEnumerable<string> names, int size, int factor)
        => theme + "\0" + string.Join('\u0001', names) + "\0" + size + "x" + factor;

    // Only what needs no work: made in this process, or in the disk cache (a few hundred microseconds).
    // False means LoadAsync or Load has to make it.
    public bool TryGetReady(IReadOnlyList<string> names, int size, double scale, out IImage? image)
    {
        int factor = Math.Max(1, (int)Math.Ceiling(scale));
        var key = Key(_theme, names, size, factor);
        if (_memory.TryGetValue(key, out image))
            return true;
        if (_useDisk && Disk.TryGet(key) is { } cached)
        {
            _memory[key] = image = cached;
            return true;
        }
        image = null;
        return false;
    }

    // The first name the theme has; a generic icon when none is found, so the UI never shows a hole.
    // size is in logical pixels, scale is the display scale.
    public IImage? Load(IReadOnlyList<string> names, int size, double scale = 1)
    {
        if (TryGetReady(names, size, scale, out var ready))
            return ready;
        return Make(names, size, scale);
    }

    public IImage? Load(IEnumerable<string> names, int size, double scale = 1) => Load(names.ToList(), size, scale);

    // Makes the icon on a worker thread; the result is in the process cache for the next ask as well. Asking for
    // the same icon while it is being made (a control sets its name, then its size, then attaches) shares the work.
    public Task<IImage?> LoadAsync(IReadOnlyList<string> names, int size, double scale = 1)
    {
        var key = Key(_theme, names, size, Math.Max(1, (int)Math.Ceiling(scale)));
        var snapshot = names.ToList();
        return _pending.GetOrAdd(key, k => Task.Run(() =>
        {
            try { return Load(snapshot, size, scale); }
            finally { _pending.TryRemove(key, out _); }
        }));
    }

    private IImage? Make(IReadOnlyList<string> names, int size, double scale)
    {
        int factor = Math.Max(1, (int)Math.Ceiling(scale));
        var key = Key(_theme, names, size, factor);
        IImage? image = null;
        foreach (var name in names.Append("application-x-generic"))
        {
            IconFile? file;
            lock (_lookupLock) file = _lookup.Find(name, size, factor, _theme);
            if (file is not null)
            {
                image = ImageFor(file.Path, size * factor);
                break;
            }
        }
        _memory[key] = image;
        if (_useDisk && image is Bitmap bitmap)
            Disk.Put(key, bitmap);
        return image;
    }

    private IconDiskCache Disk
    {
        get
        {
            if (_disk is not null) return _disk;
            string stamp;
            lock (_lookupLock) stamp = _lookup.Stamp(_theme);
            return _disk ??= new IconDiskCache(stamp);
        }
    }

    // Each picture file is made once per pixel size; two different names that end up at one file share it.
    private IImage? ImageFor(string path, int pixels)
    {
        var key = path + "@" + pixels;
        if (_files.TryGetValue(key, out var cached))
            return cached;

        IImage? image = null;
        try
        {
            image = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? SvgRasterizer.Render(path, pixels) : new Bitmap(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A broken icon file (bad XML, truncated PNG, unreadable) must never break the window that shows it.
        }
        _files[key] = image;
        return image;
    }
}
