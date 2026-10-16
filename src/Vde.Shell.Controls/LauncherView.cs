using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The start menu: a search box on top; below it the pinned apps and recent ones, or the alphabetical list of
// everything; typing replaces all of that with search results. All of it is local (see LauncherModel).
public sealed class LauncherView : UserControl
{
    private readonly LauncherModel _model;
    private readonly TextBox _search = new() { Watermark = "Search apps and settings" };
    private readonly StackPanel _body = new() { Spacing = 10 };
    private bool _showAll;

    public LauncherView(LauncherModel model)
    {
        _model = model;
        _search.TextChanged += (_, _) => Rebuild();
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_search, Dock.Top);
        layout.Children.Add(_search);
        layout.Children.Add(new ScrollViewer { Content = _body, Margin = new Thickness(0, 12, 0, 0) });
        Content = layout;
        Rebuild();
    }

    // The user chose an app (and it should be started), or a setting (and it should be opened).
    public event EventHandler<DesktopEntry>? AppInvoked;
    public event EventHandler<ControlPanelItem>? SettingInvoked;

    public string SearchText
    {
        get => _search.Text ?? "";
        set { _search.Text = value; Rebuild(); }   // rebuild now: TextChanged may come later than the caller expects
    }

    // Toggles between the home page (pinned + recent) and the full alphabetical list.
    public bool ShowAllApps
    {
        get => _showAll;
        set { _showAll = value; Rebuild(); }
    }

    // The visible rows' titles, in order: for tests and for screen readers.
    public IReadOnlyList<string> VisibleTitles => _body.GetVisualDescendants().OfType<Button>().Select(b => b.Tag as string ?? "").Where(t => t.Length > 0).ToList();

    public void Focus_() => _search.Focus();

    private void Rebuild()
    {
        _body.Children.Clear();
        var text = SearchText.Trim();

        if (text.Length > 0)
        {
            var results = _model.Search(text);
            if (results.Count == 0)
                _body.Children.Add(Muted("Nothing found on this computer."));
            foreach (var result in results)
                _body.Children.Add(Row(result.Title, result.Subtitle, result.Icon, () =>
                {
                    if (result.App is { } app) Launch(app);
                    else if (result.Setting is { } setting) SettingInvoked?.Invoke(this, setting);
                }));
            return;
        }

        if (_showAll)
        {
            foreach (var group in _model.Alphabetical())
            {
                _body.Children.Add(new TextBlock { Text = group.Heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 8, 0, 0) });
                foreach (var app in group.Apps)
                    _body.Children.Add(Row(app.Name, "", app.Icon ?? "application-x-executable", () => Launch(app)));
            }
            return;
        }

        _body.Children.Add(Heading("Pinned"));
        var pinned = _model.Pinned;
        if (pinned.Count == 0)
            _body.Children.Add(Muted("Pin apps from the list below."));
        foreach (var app in pinned)
            _body.Children.Add(Row(app.Name, "", app.Icon ?? "application-x-executable", () => Launch(app)));
        var recent = _model.Recent;
        if (recent.Count > 0)
        {
            _body.Children.Add(Heading("Recent"));
            foreach (var app in recent)
                _body.Children.Add(Row(app.Name, "", app.Icon ?? "application-x-executable", () => Launch(app)));
        }
    }

    private void Launch(DesktopEntry app)
    {
        _model.RecordLaunch(app.Id);
        AppInvoked?.Invoke(this, app);
    }

    private Control Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 8, 0, 0) };

    private Control Muted(string text) => new TextBlock { Text = text, Foreground = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray), Margin = new Thickness(4) };

    private Control Row(string title, string subtitle, string icon, Action invoke)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        if (subtitle.Length > 0)
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray) });
        var button = new Button
        {
            Tag = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new ShellIcon { IconName = icon, IconSize = 24 }, text } },
        };
        button.Click += (_, _) => invoke();
        return button;
    }
}
