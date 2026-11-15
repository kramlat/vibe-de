using Avalonia;
using Avalonia.Controls;
using Vde.Shell;
using Vde.Shell.Controls;

namespace VExplore.Windows;

// The taskbar as a borderless window docked to one screen edge: top, bottom, left or right.
// (A real panel that reserves screen space and sits above windows needs the layer-shell client; see docs/panels.md.)
public sealed class TaskbarWindow : Window
{
    private readonly TaskbarModel _model;

    public TaskbarWindow(TaskbarModel model, Action startMenu, Func<DateTime>? now = null)
    {
        _model = model;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        View = new TaskbarView(model, now);
        View.StartInvoked += (_, _) => startMenu();
        View.ButtonInvoked += (_, button) => model.Click(button);
        Content = View;
        X11WindowHints.SetType(this, X11WindowHints.Kind.Dock);
        _model.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Dock);
        Opened += (_, _) => Dock();
    }

    public TaskbarView View { get; }

    // Sizes and places the window along the model's edge of the screen it is on.
    public void Dock()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        var area = screen.Bounds;
        var edge = _model.Edge;
        var thickness = TaskbarView.Thickness;
        var pixels = (int)Math.Round(thickness * scale);
        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            Width = area.Width / scale; Height = thickness;
            Position = new PixelPoint(area.X, edge == TaskbarEdge.Top ? area.Y : area.Bottom - pixels);
        }
        else
        {
            Width = thickness; Height = area.Height / scale;
            Position = new PixelPoint(edge == TaskbarEdge.Left ? area.X : area.Right - pixels, area.Y);
        }
        View.Rebuild();
        // Keep the screen edge for the taskbar: maximised windows stop at it, and the window manager docks it there.
        long full = pixels, ax = area.X, ay = area.Y, aw = area.Width, ah = area.Height;
        switch (edge)
        {
            case TaskbarEdge.Top: X11WindowHints.SetStrut(this, 0, 0, full, 0, 0, 0, 0, 0, ax, ax + aw - 1, 0, 0); break;
            case TaskbarEdge.Left: X11WindowHints.SetStrut(this, full, 0, 0, 0, ay, ay + ah - 1, 0, 0, 0, 0, 0, 0); break;
            case TaskbarEdge.Right: X11WindowHints.SetStrut(this, 0, full, 0, 0, 0, 0, ay, ay + ah - 1, 0, 0, 0, 0); break;
            default: X11WindowHints.SetStrut(this, 0, 0, 0, full, 0, 0, 0, 0, 0, 0, ax, ax + aw - 1); break;
        }
    }
}
