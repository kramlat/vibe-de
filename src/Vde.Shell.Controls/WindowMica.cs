using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vde.Shell;

namespace Vde.Shell.Controls;

// Puts an ordinary shell window on Mica, the way Windows 11 apps sit on it: the window's background is a
// patch of the theme-washed, blurred wallpaper — exactly the wallpaper pixels that sit behind where the
// window is on the screen — and the crop follows the window when it moves or resizes. Without a wallpaper
// (or off-screen) nothing happens and the window keeps the background it already has.
public static class WindowMica
{
    // target receives the crop instead of the window background — for windows whose background must
    // stay transparent (rounded corners) but still sit on the wallpaper.
    public static void Attach(Window window, DesktopSettings? settings, Border? target = null)
    {
        if (settings is null)
            return;
        window.Opened += Apply;
        window.PositionChanged += Apply;
        window.PropertyChanged += (s, e) =>
        {
            if (e.Property == Window.ClientSizeProperty)
                Apply(s, e);
        };
        Apply(window, EventArgs.Empty);   // a window created at its final place paints right on the first frame

        void Apply(object? sender, EventArgs e)
        {
            var blurred = WallpaperMica.Source(settings.Wallpaper);
            if (blurred is null)
                return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null)
                return;
            var scale = screen.Scaling;
            if (scale <= 0)
                return;
            var area = screen.Bounds;
            // the window in screen space: position is in pixels, client size is in dips
            var rect = new Rect(window.Position.X / scale, window.Position.Y / scale,
                window.ClientSize.Width, window.ClientSize.Height);
            var crop = WallpaperMica.ScreenToWallpaper(rect, new Size(blurred.PixelSize.Width, blurred.PixelSize.Height),
                new Size(area.Width / scale, area.Height / scale), settings.Mode);
            var brush = WallpaperMica.CropBrush(blurred, crop);
            if (brush is not null)
            {
                if (target is not null) target.Background = brush;
                else window.Background = brush;
            }
        }
    }
}
