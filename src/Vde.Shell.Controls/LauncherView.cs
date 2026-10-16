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

        if (_model.Layout == StartLayout.Classic) RebuildClassic(); else RebuildModern();
    }

    // Windows 11 style: pinned grid, then recommended.
    private void RebuildModern()
    {
        _body.Children.Add(Heading("Pinned"));
        var pinned = _model.Pinned;
        if (pinned.Count == 0)
            _body.Children.Add(Muted("Pin apps from the list below."));
        else
            _body.Children.Add(Tiles(pinned));
        var recent = _model.Recent;
        if (recent.Count > 0)
        {
            _body.Children.Add(Heading("Recommended"));
            foreach (var app in recent)
                _body.Children.Add(Row(app.Name, "", app.Icon ?? "application-x-executable", () => Launch(app)));
        }
    }

    // Windows 10 style: every app in a list on the left, the pinned ones as tiles on the right.
    private void RebuildClassic()
    {
        var list = new StackPanel();
        foreach (var group in _model.Alphabetical())
        {
            list.Children.Add(new TextBlock { Text = group.Heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 8, 0, 0) });
            foreach (var app in group.Apps)
                list.Children.Add(Row(app.Name, "", app.Icon ?? "application-x-executable", () => Launch(app)));
        }
        var tiles = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        tiles.Children.Add(Heading("Pinned"));
        tiles.Children.Add(_model.Pinned.Count == 0 ? Muted("Pin apps to see them here.") : Tiles(_model.Pinned));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Height = 520 };
        grid.Children.Add(new ScrollViewer { Content = list });
        var right = new ScrollViewer { Content = tiles };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _body.Children.Add(grid);
    }

    private Control Tiles(IReadOnlyList<DesktopEntry> apps)
    {
        var wrap = new WrapPanel();
        foreach (var app in apps)
        {
            var tile = new Button
            {
                Tag = app.Name, Width = 84, Height = 84, Margin = new Thickness(2), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new ShellIcon { IconName = app.Icon ?? "application-x-executable", IconSize = 32, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = app.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 78 },
                    },
                },
            };
            var captured = app;
            tile.Click += (_, _) => Launch(captured);
            wrap.Children.Add(tile);
        }
        return wrap;
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
