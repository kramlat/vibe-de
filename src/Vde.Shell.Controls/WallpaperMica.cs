using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using SkiaSharp;
using Vde.Shell;

namespace Vde.Shell.Controls;

// The Mica material, the way Windows 11 actually builds it: not a darkened wallpaper, but a near-opaque
// neutral wash that lets only a faint, desaturated hint of the blurred desktop wallpaper through, so every
// surface sitting on it agrees on the colour while the wallpaper still "breathes" behind the shell.
// vwm has no render pass of its own to blur the live screen behind a window, and Mica is defined over the
// wallpaper anyway, so one cached bitmap serves every surface.
public static class WallpaperMica
{
    // How much of the washed-out wallpaper survives the neutral base: 0 is a plain theme surface, 1 is
    // the raw blurred picture. Windows 11 keeps this small — the wash dominates, the wallpaper whispers.
    public const double Bleed = 0.20;

    private static (string Path, int Width, double Bleed, bool Dark, Bitmap Bitmap)? _cache;
    private static readonly object _gate = new();
    private static readonly Dictionary<string, List<Action>> _waiting = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    // The washed, blurred wallpaper at `width` pixels across. Which neutral base washes over it follows the
    // application's theme variant (light: near-white, dark: near-black), the way Windows switches Mica.
    // `wallpaper` is the path from DesktopSettings; a missing or unreadable picture (the desktop then paints
    // its background colour) gives null, and callers fall back to their theme's plain surface with a scrim
    // behind label text. Complete but possibly slow the first time (a 4K picture is decoded and blurred): a
    // window uses Peek and WhenReady instead, so it opens at once and gets the backdrop a moment later.
    public static Bitmap? Source(string? wallpaper, int width = 1024, double bleed = Bleed)
    {
        if (string.IsNullOrEmpty(wallpaper) || !File.Exists(wallpaper) || width <= 0)
            return null;
        return Make(wallpaper, width, bleed, IsDark());
    }

    // What is ready without the slow part: the picture already made in this process, or its copy in the disk
    // cache (written by an earlier program). Null when it still has to be made: see WhenReady.
    public static Bitmap? Peek(string? wallpaper, int width = 1024, double bleed = Bleed)
    {
        if (string.IsNullOrEmpty(wallpaper) || !File.Exists(wallpaper) || width <= 0)
            return null;
        bool dark = IsDark();
        lock (_gate)
            if (_cache is { } hit && hit.Path == wallpaper && hit.Width == width && hit.Bleed == bleed && hit.Dark == dark)
                return hit.Bitmap;
        if (ReadDisk(wallpaper, width, bleed, dark) is { } cached)
        {
            lock (_gate) _cache = (wallpaper, width, bleed, dark, cached);
            return cached;
        }
        return null;
    }

    // Makes the picture on a worker thread and then calls `ready` (on that thread: post to the UI from it).
    // Several windows asking for the same picture share one piece of work. Not called when there is no picture.
    public static void WhenReady(string? wallpaper, Action ready, int width = 1024, double bleed = Bleed)
    {
        if (string.IsNullOrEmpty(wallpaper) || !File.Exists(wallpaper) || width <= 0)
            return;
        bool dark = IsDark();   // read here: the theme variant belongs to the UI thread
        var key = wallpaper + "|" + width + "|" + bleed + "|" + dark;
        lock (_gate)
        {
            if (_failed.Contains(key)) return;   // it could not be made before: do not try on every window move
            if (_waiting.TryGetValue(key, out var list)) { list.Add(ready); return; }
            _waiting[key] = [ready];
        }
        Task.Run(() =>
        {
            Bitmap? made = null;
            try { made = Make(wallpaper, width, bleed, dark); }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
            List<Action> callbacks;
            lock (_gate)
            {
                callbacks = _waiting[key];
                _waiting.Remove(key);
                if (made is null) _failed.Add(key);
            }
            if (made is not null)
                foreach (var callback in callbacks) callback();
        });
    }

    private static bool IsDark() => Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

    private static Bitmap? Make(string wallpaper, int width, double bleed, bool dark)
    {
        lock (_gate)
            if (_cache is { } hit && hit.Path == wallpaper && hit.Width == width && hit.Bleed == bleed && hit.Dark == dark)
                return hit.Bitmap;
        if (ReadDisk(wallpaper, width, bleed, dark) is { } cached)
        {
            lock (_gate) _cache = (wallpaper, width, bleed, dark, cached);
            return cached;
        }
        Bitmap? washed = null;
        try
        {
            using var original = DecodeSmall(wallpaper, width);
            if (original is { Width: > 0, Height: > 0 })
            {
                washed = Wash(original, width, bleed, dark);
                lock (_gate) _cache = (wallpaper, width, bleed, dark, washed);
                WriteDisk(wallpaper, width, bleed, dark, washed);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            // an unreadable image: just the colour, like the desktop does
        }
        return washed;
    }

    // The picture decoded no larger than the blur needs: a JPEG can be decoded at 1/2, 1/4 or 1/8 size, which
    // is several times faster than decoding a 4K picture and shrinking it afterwards. Other formats decode whole.
    private static SKBitmap? DecodeSmall(string path, int width)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null)
            return null;
        var full = codec.Info;
        if (full.Width > width)
        {
            var size = codec.GetScaledDimensions((float)width / full.Width);
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var small = new SKBitmap(info);
            var result = codec.GetPixels(info, small.GetPixels());
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                return small;
            small.Dispose();
        }
        return SKBitmap.Decode(path);
    }

    private static Bitmap Wash(SKBitmap original, int width, double bleed, bool dark)
    {
        int w = Math.Min(width, original.Width);
        int h = Math.Max(1, (int)Math.Round((double)original.Height * w / original.Width));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        // the blur radius grows with the (downscaled) picture, so a wide wallpaper blurs as much as a narrow
        // one; the colour matrix pulls the picture half-way to its grey, so only a muted hue survives
        float sigma = Math.Max(2f, w / 24f);
        // out = 0.5 * in + 0.5 * grey(in), grey by the sRGB luminance weights
        const float lR = 0.2126f, lG = 0.7152f, lB = 0.0722f;
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                0.5f + 0.5f * lR, 0.5f * lG,        0.5f * lB,        0, 0,
                0.5f * lR,        0.5f + 0.5f * lG, 0.5f * lB,        0, 0,
                0.5f * lR,        0.5f * lG,        0.5f + 0.5f * lB, 0, 0,
                0,                0,                0,                1, 0,
            ]),
        };
        surface.Canvas.DrawBitmap(original, new SKRect(0, 0, w, h), paint);
        // the neutral base, the same colours the theme's Surface falls back to, over most of the picture
        var base_ = dark ? SKColor.Parse("#202020") : SKColor.Parse("#F3F3F3");
        surface.Canvas.DrawColor(base_.WithAlpha((byte)Math.Round(255 * (1 - bleed))), SKBlendMode.SrcOver);
        using var image = surface.Snapshot();
        // the pixels go straight into the bitmap (no PNG encode and decode in between)
        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888);
        using (var frame = bitmap.Lock())
            image.ReadPixels(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), frame.Address, frame.RowBytes);
        return bitmap;
    }

    // ---- the disk cache: the washed picture as a PNG named by what it was made from ----

    private static string CacheFile(string wallpaper, int width, double bleed, bool dark)
    {
        var info = new FileInfo(wallpaper);
        var text = $"{wallpaper}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{width}|{bleed:R}|{dark}";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cache, "vde", "mica", "v2", Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + ".png");
    }

    private static Bitmap? ReadDisk(string wallpaper, int width, double bleed, bool dark)
    {
        try
        {
            var file = CacheFile(wallpaper, width, bleed, dark);
            return File.Exists(file) ? new Bitmap(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteDisk(string wallpaper, int width, double bleed, bool dark, Bitmap washed)
    {
        try
        {
            var file = CacheFile(wallpaper, width, bleed, dark);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + "." + Environment.ProcessId + ".tmp";
            washed.Save(temp, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    // A brush that shows `crop` (a rectangle of the blurred picture) stretched over the whole element.
    // The desktop labels and the taskbar use this to reveal the wallpaper pixels that are really behind
    // them, blurred; the ImageBrush crop keeps them aligned with the wallpaper around them.
    public static IBrush? CropBrush(Bitmap? blurred, Rect crop)
    {
        if (blurred is null || crop.Width <= 0 || crop.Height <= 0)
            return null;
        // clamp, so an icon on the screen's edge or a smaller wallpaper in Center mode stays inside the picture
        crop = crop.Intersect(new Rect(0, 0, blurred.PixelSize.Width, blurred.PixelSize.Height));
        if (crop.Width <= 0 || crop.Height <= 0)
            return null;
        return new ImageBrush(blurred)
        {
            SourceRect = new RelativeRect(crop, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative),
            Stretch = Stretch.Fill,
        };
    }

    // What part of the wallpaper the screen (or a window on it) really shows, in wallpaper pixels: the same
    // mapping WallpaperBrush's placing does, inverted. Crop a plate's screen rectangle to get its backdrop.
    public static Rect ScreenToWallpaper(Rect screenRect, Size wall, Size screen, WallpaperMode mode)
    {
        if (wall.Width <= 0 || wall.Height <= 0 || screen.Width <= 0 || screen.Height <= 0)
            return default;
        double ox, oy, sx, sy;
        switch (mode)
        {
            case WallpaperMode.Stretch:
                sx = screen.Width / wall.Width;
                sy = screen.Height / wall.Height;
                ox = oy = 0;
                break;
            case WallpaperMode.Fill:
            case WallpaperMode.Fit:
            {
                double scale = mode == WallpaperMode.Fill
                    ? Math.Max(screen.Width / wall.Width, screen.Height / wall.Height)
                    : Math.Min(screen.Width / wall.Width, screen.Height / wall.Height);
                sx = sy = scale;
                ox = (screen.Width - wall.Width * scale) / 2;
                oy = (screen.Height - wall.Height * scale) / 2;
                break;
            }
            default:   // Center and Tile: the picture at its true size, centred (and repeated for Tile)
                sx = sy = 1;
                ox = (screen.Width - wall.Width) / 2;
                oy = (screen.Height - wall.Height) / 2;
                break;
        }
        double x0 = (screenRect.X - ox) / sx, y0 = (screenRect.Y - oy) / sy;
        double x1 = x0 + screenRect.Width / sx, y1 = y0 + screenRect.Height / sy;
        if (mode == WallpaperMode.Tile)
        {
            // the repeat: only the crop's position within one picture matters
            x0 = Mod(x0, wall.Width);
            y0 = Mod(y0, wall.Height);
            x1 = x0 + screenRect.Width / sx;
            y1 = y0 + screenRect.Height / sy;
        }
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static double Mod(double v, double m)
    {
        v %= m;
        return v >= 0 ? v : v + m;
    }
}
