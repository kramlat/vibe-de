using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    // The washed, blurred wallpaper at `width` pixels across. Which neutral base washes over it follows the
    // application's theme variant (light: near-white, dark: near-black), the way Windows switches Mica.
    // `wallpaper` is the path from DesktopSettings; a missing or unreadable picture (the desktop then paints
    // its background colour) gives null, and callers fall back to their theme's plain surface with a scrim
    // behind label text.
    public static Bitmap? Source(string? wallpaper, int width = 1024, double bleed = Bleed)
    {
        if (string.IsNullOrEmpty(wallpaper) || !File.Exists(wallpaper) || width <= 0)
            return null;
        bool dark = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
        if (_cache is { } hit && hit.Path == wallpaper && hit.Width == width && hit.Bleed == bleed && hit.Dark == dark)
            return hit.Bitmap;
        Bitmap? washed = null;
        try
        {
            using var original = SKBitmap.Decode(wallpaper);
            if (original is { Width: > 0, Height: > 0 })
            {
                washed = Wash(original, width, bleed, dark);
                _cache = (wallpaper, width, bleed, dark, washed);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            // an unreadable image: just the colour, like the desktop does
        }
        return washed;
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
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var png = new MemoryStream(data.ToArray());
        return new Bitmap(png);
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
