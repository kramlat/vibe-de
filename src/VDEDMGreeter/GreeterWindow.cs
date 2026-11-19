using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Vde.DisplayManager;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace VdedmGreeter;

// The login screen: a clock, the user to log in as, the password, a message, the other users, the session to start,
// and the power buttons. It only shows a GreeterModel and forwards what is typed.
public sealed class GreeterWindow : Window
{
    private readonly GreeterModel _model;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _power;
    private readonly TextBlock _clock = new() { FontSize = 72, FontWeight = FontWeight.Light, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
    private readonly TextBlock _date = new() { FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
    private readonly ContentControl _center = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _others = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32) };
    private readonly TextBox _password = new() { Width = 280, Watermark = "Password" };
    private readonly TextBlock _message = new() { TextAlignment = TextAlignment.Center, MaxWidth = 320, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _sessions = new() { MinWidth = 200 };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    // power receives "reboot" or "poweroff".
    public GreeterWindow(GreeterModel model, IBrush? background = null, Func<DateTime>? now = null, Action<string>? power = null)
    {
        _model = model;
        _now = now ?? (() => DateTime.Now);
        _power = power ?? (_ => { });
        Title = "Login";
        SystemDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Background = background ?? new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#1B1F3B"), 0), new GradientStop(Color.Parse("#3B2A5C"), 1) },
        };

        _sessions.ItemsSource = model.Sessions.Select(s => s.Name).ToList();
        _sessions.SelectedIndex = Math.Max(0, model.Sessions.ToList().IndexOf(model.Session!));
        _sessions.IsVisible = model.Sessions.Count > 1;
        _sessions.SelectionChanged += (_, _) => { if (_sessions.SelectedIndex >= 0) model.Session = model.Sessions[_sessions.SelectedIndex]; };

        var restart = PowerButton("system-reboot", "Restart", "reboot");
        var shutdown = PowerButton("system-shutdown", "Shut down", "poweroff");
        var bottomRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32), Children = { _sessions, restart, shutdown } };
        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 64, 0, 0), Children = { _clock, _date } };

        var root = new Grid();
        root.Children.Add(top);
        root.Children.Add(_center);
        root.Children.Add(_others);
        root.Children.Add(bottomRight);
        Content = root;
        Foreground = Brushes.White;

        _password.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Submit(); } else if (e.Key == Key.Escape) { e.Handled = true; await _model.CancelAsync(); } };
        _model.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        _tick.Tick += (_, _) => UpdateClock();
        _tick.Start();
        UpdateClock();
        Rebuild();
        Opened += (_, _) => { if (_model.Users.Count == 1 && _model.Stage == GreeterStage.ChooseUser) _ = _model.BeginAsync(_model.Users[0].Name); };
    }

    public GreeterModel Model => _model;
    public TextBox PasswordBox => _password;
    public string? MessageText => _message.IsVisible ? _message.Text : null;
    public string ClockText => _clock.Text ?? "";

    public async Task Submit()
    {
        var text = _password.Text ?? "";
        _password.Text = "";
        await _model.AnswerAsync(text);
    }

    private void UpdateClock()
    {
        var now = _now();
        _clock.Text = now.ToString("t");
        _date.Text = now.ToString("D");
    }

    private Button PowerButton(string icon, string tip, string action)
    {
        var b = new Button { Content = new ShellIcon { IconName = icon, IconSize = 24 }, Padding = new Thickness(10), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => _power(action);
        return b;
    }

    // ---- what is shown ----

    // The long-lived controls (message, password box) move between panels as the stage changes: take them out of the old one first.
    private static T Detach<T>(T control) where T : Control
    {
        (control.Parent as Panel)?.Children.Remove(control);
        if (control.Parent is ContentControl cc) cc.Content = null;
        return control;
    }

    private void Rebuild()
    {
        _message.IsVisible = _model.Message is { Length: > 0 };
        _message.Text = _model.Message;
        _message.Foreground = _model.MessageIsError ? new SolidColorBrush(Color.Parse("#FF8A80")) : Brushes.White;

        if (_model.Stage == GreeterStage.ChooseUser)
        {
            ShowUserList();
            _others.Children.Clear();
            return;
        }
        var user = _model.Users.FirstOrDefault(u => u.Name == _model.UserName);
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Avatar(user, _model.UserName ?? "", 128));
        panel.Children.Add(new TextBlock { Text = user is { FullName.Length: > 0 } ? user.FullName : _model.UserName, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White });
        if (_model.Stage == GreeterStage.Prompt)
        {
            _password.PasswordChar = _model.PromptSecret ? '•' : '\0';
            _password.Watermark = _model.PromptText;
            var go = new Button { Content = "→", Padding = new Thickness(12, 6) };
            go.Click += async (_, _) => await Submit();
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { Detach(_password), go } });
            Dispatcher.UIThread.Post(() => _password.Focus(), DispatcherPriority.Background);
        }
        else if (_model.Stage == GreeterStage.Working || _model.Stage == GreeterStage.Done)
            panel.Children.Add(new TextBlock { Text = _model.Stage == GreeterStage.Done ? "Starting your desktop…" : "Please wait…", HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(Detach(_message));
        _center.Content = panel;

        _others.Children.Clear();
        if (_model.Stage == GreeterStage.Prompt)
        {
            var back = new Button { Content = "Not you? Choose another user", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White };
            back.Click += async (_, _) => await _model.CancelAsync();
            _others.Children.Add(back);
        }
    }

    private void ShowUserList()
    {
        var tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 720 };
        foreach (var user in _model.Users)
        {
            var captured = user;
            var tile = new Button
            {
                Tag = user.Name, Margin = new Thickness(10), Padding = new Thickness(14), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                Content = new StackPanel { Spacing = 8, Width = 140, Children = { Avatar(user, user.Name, 96), new TextBlock { Text = user.FullName.Length > 0 ? user.FullName : user.Name, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White } } },
            };
            tile.Click += async (_, _) => await _model.BeginAsync(captured.Name);
            tiles.Children.Add(tile);
        }
        var other = new Button { Content = "Other user…", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes.White };
        other.Click += async (_, _) =>
        {
            var name = new TextBox { Width = 240, Watermark = "User name" };
            var dialog = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Log in as", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center }, name } };
            _center.Content = dialog;
            name.KeyDown += async (_, e) => { if (e.Key == Key.Enter && (name.Text ?? "").Trim() is { Length: > 0 } typed) { e.Handled = true; await _model.BeginAsync(typed); } else if (e.Key == Key.Escape) { e.Handled = true; ShowUserList(); } };
            name.Focus();
            await Task.CompletedTask;
        };
        _center.Content = new StackPanel { Spacing = 10, Children = { tiles, other, Detach(_message) } };
    }

    private static Control Avatar(LoginUser? user, string name, double size)
    {
        var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        var circle = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse("#5B6CFF")) };
        grid.Children.Add(circle);
        if (user?.Icon() is { } path)
        {
            try { circle.Fill = new ImageBrush(new Bitmap(path)) { Stretch = Stretch.UniformToFill }; return grid; }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { }
        }
        grid.Children.Add(new TextBlock { Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?", FontSize = size / 2.2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        return grid;
    }
}
