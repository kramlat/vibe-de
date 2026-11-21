using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell.Notifications;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The notification centre: a header (title, do-not-disturb, Clear all), then the waiting notifications grouped by
// application, newest group first. A group is one card showing its newest notification; "+N notifications" opens
// the rest. Everything follows the NotificationCenterModel and rebuilds when it changes.
public sealed class NotificationCenterView : UserControl
{
    private readonly NotificationCenterModel _model;
    private readonly StackPanel _list = new() { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };
    private readonly Func<DateTime> _now;
    private readonly ToggleButton _dnd;
    private readonly Button _clear;
    private readonly StackPanel _notice = new();

    public NotificationCenterView(NotificationCenterModel model, Func<DateTime>? now = null)
    {
        _model = model;
        _now = now ?? (() => DateTime.Now);
        Width = 408;

        var title = new TextBlock { Text = "Notifications", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _dnd = new ToggleButton { Content = new ShellIcon { IconNames = ["notifications-disabled", "preferences-system-notifications"], IconSize = 16 }, Padding = new Thickness(8, 6), Tag = "DoNotDisturb" };
        ToolTip.SetTip(_dnd, "Do not disturb");
        _dnd.IsCheckedChanged += (_, _) => { if (_dnd.IsChecked != _model.DoNotDisturb) _model.DoNotDisturb = _dnd.IsChecked == true; };
        _clear = new Button { Content = "Clear all", Padding = new Thickness(14, 5), Tag = "ClearAll" };
        _clear.Click += (_, _) => _model.ClearAll();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _dnd, _clear } };
        var header = new DockPanel { Margin = new Thickness(16, 12, 12, 8), Children = { buttons, title } };
        DockPanel.SetDock(buttons, Dock.Right);

        var body = new StackPanel { Children = { _notice, _list } };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };
        Content = new StackPanel { Children = { header, scroll } };

        model.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public IReadOnlyList<Control> Cards => _list.Children.OfType<Border>().Where(b => b.Tag is string).ToList();
    public bool IsEmpty => _model.Count == 0;
    public Button ClearButton => _clear;
    public ToggleButton DoNotDisturbButton => _dnd;

    private IBrush Token(ThemeToken token, IBrush fallback) => ThemeTokens.Find(this, token, fallback);

    private void Rebuild()
    {
        _dnd.IsChecked = _model.DoNotDisturb;
        _notice.Children.Clear();
        if (_model.DoNotDisturb)
            _notice.Children.Add(new StackPanel
            {
                Margin = new Thickness(16, 4, 16, 12), Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Do not disturb is on", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "You'll only see banners for urgent notifications to help you stay focused.", TextWrapping = TextWrapping.Wrap, Foreground = Token(ThemeToken.TextSecondary, Brushes.Gray) },
                },
            });

        _list.Children.Clear();
        var groups = _model.Groups();
        _clear.IsEnabled = groups.Count > 0;
        if (groups.Count == 0)
        {
            _list.Children.Add(new Border
            {
                Padding = new Thickness(0, 48),
                Child = new TextBlock { Text = "No new notifications", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Token(ThemeToken.TextSecondary, Brushes.Gray) },
            });
            return;
        }
        foreach (var group in groups)
        {
            _list.Children.Add(AppHeader(group));
            _list.Children.Add(Card(group));
        }
    }

    private Control AppHeader(NotificationGroup group)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 10, 0, 2) };
        row.Children.Add(new ShellIcon { IconNames = group.AppIcon.Length > 0 ? [group.AppIcon, "preferences-system-notifications"] : ["preferences-system-notifications"], IconSize = 16 });
        row.Children.Add(new TextBlock { Text = group.AppName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Border Card(NotificationGroup group)
    {
        var stack = new StackPanel { Spacing = 6 };
        foreach (var n in group.Shown)
            stack.Children.Add(Item(group, n, first: n == group.Newest));
        if (group.Items.Count > 1)
        {
            var more = new Button
            {
                Content = group.Expanded ? "Show less" : $"+{group.Hidden} notification{(group.Hidden == 1 ? "" : "s")}",
                Padding = new Thickness(12, 4), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "More",
            };
            more.Click += (_, _) => _model.ToggleExpanded(group.AppName);
            stack.Children.Add(more);
        }
        return new Border
        {
            Padding = new Thickness(12, 8, 12, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Token(ThemeToken.Border, Brushes.Gray),
            Background = Token(ThemeToken.HoverFill, Brushes.Transparent),
            Tag = group.AppName,
            Child = stack,
        };
    }

    private Control Item(NotificationGroup group, Notification n, bool first)
    {
        var secondary = Token(ThemeToken.TextSecondary, Brushes.Gray);
        var close = new Button { Content = "✕", Padding = new Thickness(6, 2), FontSize = 11, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Tag = "Dismiss" };
        ToolTip.SetTip(close, "Dismiss");
        close.Click += (_, _) => _model.Dismiss(n.Id);
        var time = new TextBlock { Text = TimeText(n.Updated), FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel { Children = { close, time } };
        DockPanel.SetDock(close, Dock.Right);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = n.Summary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (n.PlainBody.Length > 0)
            text.Children.Add(new TextBlock { Text = n.PlainBody, Foreground = secondary, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });

        var item = new StackPanel { Spacing = 4, Children = { top, text } };
        var actions = n.Actions.Where(a => a.Key != NotificationAction.Default).ToList();
        if (actions.Count > 0)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var a in actions)
            {
                var button = new Button { Content = a.Label, Padding = new Thickness(12, 4), FontSize = 12 };
                var key = a.Key;
                button.Click += (_, _) => _model.Invoke(n, key);
                bar.Children.Add(button);
            }
            item.Children.Add(bar);
        }
        // a click on the text is the notification's default action
        text.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) _model.Activate(n); };
        return item;
    }

    // "11:05 AM" today, the date for older ones.
    private string TimeText(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
        var today = _now().Date;
        return local.Date == today ? local.ToString("t") : local.Date == today.AddDays(-1) ? "Yesterday " + local.ToString("t") : local.ToString("d") + " " + local.ToString("t");
    }
}
