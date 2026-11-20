using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    // Breeze draws its 16px mimetypes around a y of 1036 and writes the shift as translate(0-1036.36),
    // which SvgSource misreads (it answers with an off-canvas picture). A comma between the arguments
    // parses right, so we add the missing separator before parsing.
    private static readonly Regex UnseparatedArgs = new(
        @"\b(translate|scale|rotate)\(\s*(-?\d+(?:\.\d+)?)\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    // The file's text through the normalization above, parsed from a stream so the rewritten text is
    // what SvgSource sees.
    private static SvgSource? LoadSource(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(UnseparatedArgs.Replace(text, "$1($2,$3")));
            return SvgSource.LoadFromStream(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // pixels: the side of the square the icon is drawn into (the picture keeps its aspect ratio inside it).
    public static Bitmap? Render(string path, int pixels = RasterPixels)
    {
        pixels = Math.Clamp(pixels, 8, 1024);
        var source = LoadSource(path);
        if (source?.Picture is not { } picture)
            return null;
        var cull = picture.CullRect;
        if (cull.Width <= 0 || cull.Height <= 0)
            return null;

        double uniform = Math.Min(pixels / cull.Width, pixels / cull.Height);
        var matrix = SKMatrix.CreateScale((float)uniform, (float)uniform);
        using var image = SKImage.FromPicture(picture, new SKSizeI(pixels, pixels), matrix);
        if (image is null)
            return null;

        // The pixels go straight into the bitmap: the earlier path encoded a 256px PNG and decoded it
        // again, which every app window paid once per icon on its first frame.
        var bitmap = new WriteableBitmap(new PixelSize(pixels, pixels), new Vector(96, 96), PixelFormats.Bgra8888);
        try
        {
            using (var frame = bitmap.Lock())
                image.ReadPixels(new SKImageInfo(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul), frame.Address, frame.RowBytes);
            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            return null;
        }
    }
}
