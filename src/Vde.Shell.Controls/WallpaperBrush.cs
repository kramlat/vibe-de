using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vde.Shell;

namespace Vde.Shell.Controls;

// What the desktop (and the preview in its settings page) paints: the wallpaper placed as the mode says, or
// the background colour when there is none or it cannot be read.
public static class WallpaperBrush
{
    public static IBrush Create(DesktopSettings settings)
    {
        var color = Color.TryParse(settings.BackgroundColor, out var c) ? c : Color.Parse("#1E1E2E");
        if (settings.Wallpaper is { } path && File.Exists(path))
            try
            {
                return new ImageBrush(new Bitmap(path))
                {
                    Stretch = settings.Mode switch { WallpaperMode.Fit => Stretch.Uniform, WallpaperMode.Stretch => Stretch.Fill, WallpaperMode.Center => Stretch.None, WallpaperMode.Tile => Stretch.None, _ => Stretch.UniformToFill },
                    TileMode = settings.Mode == WallpaperMode.Tile ? TileMode.Tile : TileMode.None,
                    AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
                };
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { }   // an unreadable image: just the colour
        return new SolidColorBrush(color);
    }
}
