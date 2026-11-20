using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace VExplore.Windows;

// The window a long copy or move shows, in Windows 11's form: what is being copied and where, the percentage
// with pause and cancel, the speed graph, the name of the file in hand, the time left and the items left.
// "Fewer details" folds it down to a progress bar. It follows a FileJob on a timer; closing it cancels the job,
// and it closes itself when the job is over.
public sealed class TransferWindow : Window
{
    private readonly FileJob _job;
    private readonly Action<string>? _openFolder;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _percent = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause;
    private readonly Path _pauseShape;
    private readonly SpeedGraphView _graph = new();
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _time = new();
    private readonly TextBlock _items = new();
    private readonly StackPanel _details = new() { Spacing = 8 };
    private readonly Button _toggle;
    private readonly TextBlock _toggleText = new() { Text = "Fewer details", VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _chevron = new() { Stroke = Brushes.Gray, StrokeThickness = 1.5, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, Data = Geometry.Parse("M0,6 L5,1 L10,6") };
    private bool _expanded = true;

    public TransferWindow(FileJob job, Action<string>? openFolder = null)
    {
        _job = job;
        _openFolder = openFolder;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "0% complete";

        _pauseShape = new Path { Fill = Foreground, Stretch = Stretch.None, Data = Geometry.Parse("M0,0 L4,0 L4,12 L0,12 Z M8,0 L12,0 L12,12 L8,12 Z") };
        _pause = IconButton(_pauseShape, "Pause");
        _pause.Click += (_, _) => { if (_job.State == JobState.Paused) _job.Resume(); else _job.Pause(); Refresh(); };
        var cancel = IconButton(new Path { Fill = Foreground, Stretch = Stretch.None, Data = Geometry.Parse("M1,0 L6,5 L11,0 L12,1 L7,6 L12,11 L11,12 L6,7 L1,12 L0,11 L5,6 L0,1 Z") }, "Cancel");
        cancel.Click += (_, _) => _job.Cancel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _pause, cancel } };
        var percentRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        percentRow.Children.Add(buttons);
        percentRow.Children.Add(_percent);

        _details.Children.Add(_graph);
        _details.Children.Add(new StackPanel { Spacing = 2, Children = { Line("Name: ", _name), Line("Time remaining: ", _time), Line("Items remaining: ", _items) } });

        _toggle = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _chevron, _toggleText } }, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 8) };
        _toggle.Click += (_, _) => { _expanded = !_expanded; Refresh(); };

        var body = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 0), Spacing = 10,
            Children = { Headline(), percentRow, _bar, _details },
        };
        var footer = new Border
        {
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(24, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            Child = _toggle,
        };
        Content = new StackPanel { Children = { body, footer } };

        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { _timer.Start(); Refresh(); };
        Closing += (_, _) =>
        {
            _timer.Stop();
            if (_job.State is JobState.Running or JobState.Paused)
                _job.Cancel();   // closing the window stops the copy, as in Windows
        };
        _ = _job.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(Close));
        Refresh();
    }

    public string PercentText => _percent.Text ?? "";
    public bool ShowsDetails => _expanded;
    public string TimeText => _time.Text ?? "";
    public string ItemsText => _items.Text ?? "";
    public string NameText => _name.Text ?? "";
    public SpeedGraphView Graph => _graph;
    public Button PauseButton => _pause;
    public Button ToggleButton => _toggle;

    // "Copying 8 items from Captures to Video clips", the two folder names being links.
    private Control Headline()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = $"{TransferText.Verb(_job.Move)} {TransferText.Items(_job.ItemCount)} from ", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(Link(_job.SourceFolder));
        panel.Children.Add(new TextBlock { Text = " to ", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(Link(_job.Destination));
        return panel;
    }

    private Button Link(string folder)
    {
        var text = new TextBlock { Text = TransferText.FolderName(folder), Foreground = ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue) };
        var button = new Button { Content = text, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Tag = folder, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, folder);
        button.PointerEntered += (_, _) => text.TextDecorations = TextDecorations.Underline;
        button.PointerExited += (_, _) => text.TextDecorations = null;
        button.Click += (_, _) => _openFolder?.Invoke(folder);
        return button;
    }

    private static Button IconButton(Control shape, string tip)
    {
        var button = new Button { Content = shape, Width = 36, Height = 32, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static Control Line(string label, TextBlock value)
        => new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = label }, value } };

    // Everything the window shows, from the job's monitor.
    public void Refresh()
    {
        var m = _job.Monitor;
        _percent.Text = $"{m.Percent}% complete";
        Title = _percent.Text;
        _bar.Value = m.Percent;
        _name.Text = m.CurrentName;
        _time.Text = TransferText.Remaining(m.Remaining);
        _items.Text = TransferText.ItemsRemaining(m.ItemsRemaining, m.BytesRemaining);
        _graph.Points = m.History.Select(h => (h.Fraction, h.BytesPerSecond)).ToList();
        _graph.Progress = m.Fraction;
        _graph.Caption = "Speed: " + TransferText.Speed(m.BytesPerSecond);
        _details.IsVisible = _expanded;
        _bar.IsVisible = !_expanded;
        _toggleText.Text = _expanded ? "Fewer details" : "More details";
        _chevron.Data = Geometry.Parse(_expanded ? "M0,6 L5,1 L10,6" : "M0,1 L5,6 L10,1");
        _chevron.Stroke = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        bool paused = _job.State == JobState.Paused;
        _pauseShape.Data = Geometry.Parse(paused ? "M0,0 L11,6 L0,12 Z" : "M0,0 L4,0 L4,12 L0,12 Z M8,0 L12,0 L12,12 L8,12 Z");
        ToolTip.SetTip(_pause, paused ? "Resume" : "Pause");
    }

    // Opens the window only when the job is still running after `delay`: a copy of a few small files is done
    // before anyone could read it. Returns when the job is over either way.
    public static async Task ShowIfSlowAsync(FileJob job, Window? owner, Action<string>? openFolder = null, TimeSpan? delay = null)
    {
        await Task.WhenAny(job.Completion, Task.Delay(delay ?? TimeSpan.FromMilliseconds(700)));
        if (!job.Completion.IsCompleted)
        {
            var window = new TransferWindow(job, openFolder);
            if (owner is not null) window.Show(owner); else window.Show();
        }
        await job.Completion;
    }
}
