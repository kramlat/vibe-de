using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vde.Freedesktop;
using Vde.Shell.Controls;

namespace Vde.Compositor;

// Draws window titlebars for vwm with the VDE theme. Needs a running Avalonia application (a headless one is
// fine); call from the UI thread only.
public sealed class ChromeRenderer
{
    private readonly DesktopEntryDatabase? _apps;
    private readonly Window _host = new() { SystemDecorations = SystemDecorations.None, Background = Brushes.Transparent, Height = WindowChromeView.BarHeight, Width = 800, ShowInTaskbar = false };
    private readonly WindowChromeView _view = new();
    private readonly Window _menuHost = new() { SystemDecorations = SystemDecorations.None, Background = Brushes.Transparent, Width = WindowMenuView.Width_, Height = WindowMenuView.Height_, ShowInTaskbar = false };
    private readonly WindowMenuView _menu = new();

    public ChromeRenderer(DesktopEntryDatabase? apps = null)
    {
        _apps = apps;
        _host.Content = _view;
        _host.Show();   // attached to a visual tree, so icons and theme resources resolve
        _menuHost.Content = _menu;
        _menuHost.Show();
    }

    // Premultiplied BGRA, request.Width x WindowChromeView.BarHeight.
    public byte[] Render(ChromeRequest request)
    {
        var width = Math.Max(request.Width, 1);
        _host.Width = width;
        _view.Title = request.Title;
        _view.IconNames = IconFor(request.AppId);
        _view.Active = request.Active;
        _view.Maximized = request.Maximized;
        _view.Hover = request.Hover switch { "min" => ChromeButton.Minimize, "max" => ChromeButton.Maximize, "close" => ChromeButton.Close, _ => ChromeButton.None };
        _view.Pressed = request.Pressed;
        _host.UpdateLayout();

        return Capture(_view, width, WindowChromeView.BarHeight);
    }

    // RenderTargetBitmap is premultiplied BGRA, which is what vwm wants.
    private static byte[] Capture(Control visual, int width, int height)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();   // custom-drawn parts are only recorded on a render tick
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* p = pixels)
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (IntPtr)p, pixels.Length, width * 4);
        }
        return pixels;
    }

    // Premultiplied BGRA, WindowMenuView.Width_ x WindowMenuView.Height_.
    public byte[] RenderMenu(MenuRequest request)
    {
        _menu.Enabled = request.Enabled;
        _menu.Hover = request.Hover;
        _menuHost.UpdateLayout();
        return Capture(_menu, WindowMenuView.Width_, WindowMenuView.Height_);
    }

    private IReadOnlyList<string>? IconFor(string appId)
    {
        if (_apps is null || appId.Length == 0) return null;
        var entry = _apps.Find(appId + ".desktop") ?? _apps.Find(appId)
            ?? _apps.Entries.FirstOrDefault(e => string.Equals(e.StartupWmClass, appId, StringComparison.OrdinalIgnoreCase));
        return entry?.Icon is { Length: > 0 } icon ? [icon, "application-x-executable"] : ["application-x-executable"];
    }
}
