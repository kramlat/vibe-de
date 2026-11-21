using Avalonia;
using Avalonia.Controls;
using Vde.Compositor;
using Vde.Shell;
using Vde.Shell.Controls;

namespace VExplore.Windows;

// The Start menu as a popup that belongs to the taskbar's process: the Start button, the Start key and Super+Space all
// toggle it, it opens against the taskbar (on the side the Start button is) and closes when you click away or press
// Escape. No separate program is started. vwm places the window (it is told where before it is shown); on another
// window manager the window just asks for the position.
internal sealed class StartMenuController
{
    private const string WindowTitle = "Start";
    private readonly Func<LauncherWindow> _create;
    private readonly TaskbarWindow _taskbar;
    private readonly TaskbarModel _model;
    private readonly VwmClient? _vwm;
    private LauncherWindow? _open;
    private DateTime _closedAt = DateTime.MinValue;

    public StartMenuController(Func<LauncherWindow> create, TaskbarWindow taskbar, TaskbarModel model, VwmClient? vwm)
    {
        _create = create;
        _taskbar = taskbar;
        _model = model;
        _vwm = vwm is { IsConnected: true } ? vwm : null;
        if (_vwm is not null)
        {
            _vwm.StartRequested += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Toggle);
            _vwm.HandleStart();
        }
    }

    public bool IsOpen => _open is not null;

    public void Toggle()
    {
        if (_open is not null) { _open.Close(); return; }
        // Clicking the Start button while the menu is open first takes the focus from it (which closes it) and then
        // clicks: that click must not open it again.
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(300)) return;
        Show();
    }

    public void Show(string? search = null)
    {
        if (_open is not null) return;
        var window = _create();
        window.Title = WindowTitle;
        var screen = _taskbar.Screens.ScreenFromWindow(_taskbar) ?? _taskbar.Screens.Primary;
        if (screen is not null)
        {
            var scale = screen.Scaling;
            var bounds = new PixelBox(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var (x, y) = StartMenuPlacement.Compute(bounds, _model.Edge, _model.Alignment, (int)Math.Round(TaskbarView.Thickness * scale),
                (int)Math.Round(window.Width * scale), (int)Math.Round(window.Height * scale));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
            _vwm?.AnnouncePopup((uint)Environment.ProcessId, WindowTitle, x, y);
        }
        window.Closed += (_, _) => { _open = null; _closedAt = DateTime.UtcNow; };
        _open = window;
        if (search is not null) window.View.SearchText = search;
        window.Show();
    }
}
