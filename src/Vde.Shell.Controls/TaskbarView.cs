using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The taskbar: start button, app buttons (pinned and running), a tray slot and the clock. It lays itself out
// along the model's edge, in a row for the top and bottom and in a column for the left and right.
public sealed class TaskbarView : UserControl
{
    public const int Thickness = 48;

    private readonly TaskbarModel _model;
    private readonly Func<DateTime> _now;
    private readonly Button _start = new() { Tag = "Start" };
    private readonly StackPanel _apps = new() { Spacing = 2 };
    private readonly TextBlock _clock = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextAlignment = TextAlignment.Center };
    private readonly Border _bar = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private IBrush? _mica;   // the wallpaper backdrop, or null for the theme's plain surface

    public TaskbarView(TaskbarModel model, Func<DateTime>? now = null)
    {
        _model = model;
        _now = now ?? (() => DateTime.Now);
        _start.Content = new ShellIcon { IconName = "start-here-vde", IconSize = 24 };
        _start.Click += (_, _) => StartInvoked?.Invoke(this, EventArgs.Empty);
        _start.Background = Brushes.Transparent;
        _start.BorderThickness = new Thickness(0);
        TrayArea = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = _bar;
        _timer.Tick += (_, _) => UpdateClock();
        _model.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public event EventHandler? StartInvoked;
    // A button was clicked; the model's Click result says what happened (ChooseWindow means show the windows).
    public event EventHandler<TaskbarButton>? ButtonInvoked;

    public Button StartButton => _start;
    // Where notification and status icons go.
    public StackPanel TrayArea { get; }
    public string ClockText => _clock.Text ?? "";

    // The app buttons in order: for tests and screen readers.
    public IReadOnlyList<Button> AppButtons => _apps.Children.OfType<Button>().ToList();

    // The panel's Mica: a blurred, tinted sample of the wallpaper (WallpaperMica), or null for the theme's
    // plain surface. Set once by the shell hosting the taskbar; it survives Rebuild.
    public void SetBackdrop(IBrush? mica)
    {
        _mica = mica;
        _bar.Background = mica ?? ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Black);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
        ThemeTokens.Changed += OnTheme;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        ThemeTokens.Changed -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => Rebuild();

    public void Rebuild()
    {
        var vertical = _model.IsVertical;
        var orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _apps.Orientation = orientation;
        _apps.Children.Clear();
        foreach (var button in _model.Buttons())
            _apps.Children.Add(MakeButton(button, vertical));

        foreach (var c in new Control[] { _start, _apps, TrayArea, _clock })
            (c.Parent as Panel)?.Children.Remove(c);

        _start.Width = _start.Height = 40;
        TrayArea.Orientation = orientation;
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(vertical ? 4 : 8, vertical ? 8 : 4) };
        var tail = new StackPanel { Orientation = orientation, Spacing = 4 };
        tail.Children.Add(TrayArea);
        tail.Children.Add(_clock);
        DockPanel.SetDock(tail, vertical ? Dock.Bottom : Dock.Right);
        dock.Children.Add(tail);

        // The launcher button and the task list move together: centred on the bar, or packed at its start.
        var group = new StackPanel { Orientation = orientation, Spacing = 2 };
        group.Children.Add(_start);
        group.Children.Add(_apps);
        var centered = _model.Alignment == TaskbarAlignment.Center;
        group.HorizontalAlignment = vertical ? HorizontalAlignment.Center : (centered ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        group.VerticalAlignment = vertical ? (centered ? VerticalAlignment.Center : VerticalAlignment.Top) : VerticalAlignment.Center;
        dock.Children.Add(group);

        _bar.Child = dock;
        _bar.Background = _mica ?? ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Black);
        UpdateClock();
    }

    private Button MakeButton(TaskbarButton model, bool vertical)
    {
        var grid = new Grid();
        grid.Children.Add(new ShellIcon { IconNames = model.IconNames, IconSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (model.IsRunning)
        {
            // A small bar on the side that faces the screen's inside edge... drawn on the far side from the edge it docks to.
            var accent = ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue);
            var length = model.IsActive ? 16 : 6;
            var indicator = new Border
            {
                Background = accent, CornerRadius = new CornerRadius(2),
                Width = vertical ? 3 : length, Height = vertical ? length : 3,
                HorizontalAlignment = vertical ? (_model.Edge == TaskbarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Center,
                VerticalAlignment = vertical ? VerticalAlignment.Center : (_model.Edge == TaskbarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom),
            };
            grid.Children.Add(indicator);
        }
        var button = new Button
        {
            Content = grid, Width = 40, Height = 40, Padding = new Thickness(0), BorderThickness = new Thickness(0), Tag = model,
            Background = model.IsActive ? ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.Transparent) : Brushes.Transparent,
        };
        ToolTip.SetTip(button, model.Windows.Count > 1 ? $"{model.Name} ({model.Windows.Count} windows)" : model.Windows.FirstOrDefault()?.Title ?? model.Name);
        button.Click += (_, _) => ButtonInvoked?.Invoke(this, model);
        return button;
    }

    private void UpdateClock()
    {
        var now = _now();
        _clock.Text = _model.IsVertical ? now.ToString("HH\nmm") : now.ToString("t") + "\n" + now.ToString("d");
    }
}
