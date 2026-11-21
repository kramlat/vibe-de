using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Vde.Compositor;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// The widgets panel as a popup of the taskbar's process, like the Start menu: the Widgets button toggles it, it opens at the left of the
// screen above the taskbar and closes when you click away or press Escape.
internal sealed class WidgetsController
{
    private const string WindowTitle = "Widgets";
    private readonly Func<WidgetsPanelView> _panel;
    private readonly TaskbarWindow _taskbar;
    private readonly TaskbarModel _model;
    private readonly VwmClient? _vwm;
    private Window? _open;
    private DateTime _closedAt = DateTime.MinValue;

    public WidgetsController(Func<WidgetsPanelView> panel, TaskbarWindow taskbar, TaskbarModel model, VwmClient? vwm)
    {
        _panel = panel; _taskbar = taskbar; _model = model;
        _vwm = vwm is { IsConnected: true } ? vwm : null;
    }

    public bool IsOpen => _open is not null;

    public void Toggle()
    {
        if (_open is not null) { _open.Close(); return; }
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(300)) return;   // the click that took the focus away must not reopen it
        Show();
    }

    public void Show()
    {
        if (_open is not null) return;
        var screen = _taskbar.Screens.ScreenFromWindow(_taskbar) ?? _taskbar.Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        var (x, y, height) = screen is null ? (0, 0, 800)
            : WidgetsPlacement.Compute(new PixelBox(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height), _model.Edge,
                (int)Math.Round(TaskbarView.Thickness * scale), (int)Math.Round(WidgetsPanelView.PanelWidth * scale));
        var window = new Window
        {
            Title = WindowTitle, Width = WidgetsPanelView.PanelWidth, Height = height / scale, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Topmost = true, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(x, y),
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent], Background = Brushes.Transparent,
        };
        var body = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _panel() };
        window.Opened += (_, _) => body.Background = ThemeTokens.Find(window, ThemeToken.Surface, Brushes.Black);
        window.Content = body;
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        window.Deactivated += (_, _) => window.Close();
        window.Closed += (_, _) => { _open = null; _closedAt = DateTime.UtcNow; };
        _vwm?.AnnouncePopup((uint)Environment.ProcessId, WindowTitle, x, y);
        _open = window;
        window.Show();
    }
}
