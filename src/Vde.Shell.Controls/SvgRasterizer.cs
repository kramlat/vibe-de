using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using SkiaSharp;

namespace Vde.Shell.Controls;

// Avalonia.Svg.Skia 11.3 ships draw code compiled against Avalonia 11's custom-draw pipeline
// (Avalonia.Platform.IOptionalFeatureProvider moved to Avalonia in 12, and its SvgImage draw
// op references the old names, so it fails to load and renders nothing under Avalonia 12).
// We keep SvgSource for parsing, but rasterize the SKPicture to a plain Bitmap ourselves.
// Rasterizing at RasterPixels keeps one cached bitmap per file sharp at any icon size.
internal static class SvgRasterizer
{
    public const int RasterPixels = 256;

    public static Bitmap? Render(string path)
    {
        var source = SvgSource.Load(path, null);
        if (source?.Picture is not { } picture)
            return null;
        var cull = picture.CullRect;
        if (cull.Width <= 0 || cull.Height <= 0)
            return null;

        double uniform = Math.Min(RasterPixels / cull.Width, RasterPixels / cull.Height);
        var matrix = SKMatrix.CreateScale((float)uniform, (float)uniform);
        using var image = SKImage.FromPicture(picture, new SKSizeI(RasterPixels, RasterPixels), matrix);
        if (image is null)
            return null;
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null || data.IsEmpty)
            return null;
        using var png = new MemoryStream(data.ToArray());
        return new Bitmap(png);
    }
}
