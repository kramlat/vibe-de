using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The start menu: a search box on top; below it the pinned apps and recent ones, or the alphabetical list of
// everything; typing replaces all of that with search results. All of it is local (see LauncherModel).
// What the footer's buttons ask the shell to do (the footer itself does none of it).
public enum LauncherFooterAction { Files, Settings, SignOut, Sleep, Restart, ShutDown }

public sealed class LauncherView : UserControl
{
    private readonly LauncherModel _model;
    private readonly TextBox _search = new() { PlaceholderText = "Search apps and settings" };
    private readonly StackPanel _body = new() { Spacing = 10 };
    private bool _showAll;

    private readonly StackPanel _powerChoices = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) };

    public LauncherView(LauncherModel model, UserProfile? user = null)
    {
        _model = model;
        _search.TextChanged += (_, _) => Rebuild();
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_search, Dock.Top);
        layout.Children.Add(_search);
        var footer = Footer(user ?? new UserProfile());
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = _body, Margin = new Thickness(0, 12, 0, 0) });
        Content = layout;
        Rebuild();
    }

    // The user chose an app (and it should be started), or a setting (and it should be opened).
    public event EventHandler<DesktopEntry>? AppInvoked;
    public event EventHandler<ControlPanelItem>? SettingInvoked;
    public event EventHandler<LauncherFooterAction>? FooterInvoked;

    // Whether the Sign out / Sleep / Restart / Shut down choices are showing (the power button toggles them).
    public bool PowerChoicesVisible => _powerChoices.IsVisible;
    public string UserDisplayName { get; private set; } = "";

    // Presses a footer button by its action, as a click would (for tests and for the keyboard).
    public void InvokeFooter(LauncherFooterAction action) => FooterInvoked?.Invoke(this, action);

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

    // Windows 11 style: pinned grid under an "All apps" button, then a two-column recommended list.
    private void RebuildModern()
    {
        _body.Children.Add(HeadingWith("Pinned", SideLink("All apps")));
        var pinned = _model.Pinned;
        if (pinned.Count == 0)
            _body.Children.Add(Muted("Pin apps from the list below."));
        else
            _body.Children.Add(Tiles(pinned));
        var recent = _model.Recent;
        if (recent.Count > 0)
        {
            _body.Children.Add(HeadingWith("Recommended", SideLink("More")));
            var grid = new UniformGrid { Columns = 2 };
            foreach (var app in recent)
                grid.Children.Add(Row(app.Name, app.Comment ?? "Recently launched", app.Icon ?? "application-x-executable", () => Launch(app)));
            _body.Children.Add(grid);
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

    // Windows 11's start menu footer: who you are on the left; files, settings and power on the right.
    private Control Footer(UserProfile user)
    {
        UserDisplayName = user.DisplayName;

        Control avatar;
        IBrush fill = ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray);
        try { avatar = Badge(user.PicturePath is { } picture ? new ImageBrush(new Bitmap(picture)) { Stretch = Stretch.UniformToFill } : null, user.Initial, fill); }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { avatar = Badge(null, user.Initial, fill); }

        var who = new Button
        {
            Tag = "user", Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { avatar, new TextBlock { Text = user.DisplayName, VerticalAlignment = VerticalAlignment.Center } } },
        };
        who.Click += (_, _) => InvokeFooter(LauncherFooterAction.Settings);   // the account's settings live there

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(FooterButton("Files", "Files", ["system-file-manager", "folder"], LauncherFooterAction.Files));
        buttons.Children.Add(FooterButton("Settings", "Settings", ["preferences-system", "configure"], LauncherFooterAction.Settings));
        var power = FooterButton("Power", "Power", ["system-shutdown"], null);
        power.Click += (_, _) => _powerChoices.IsVisible = !_powerChoices.IsVisible;
        buttons.Children.Add(power);

        foreach (var (text, action) in new[] { ("Sign out", LauncherFooterAction.SignOut), ("Sleep", LauncherFooterAction.Sleep), ("Restart", LauncherFooterAction.Restart), ("Shut down", LauncherFooterAction.ShutDown) })
        {
            var choice = new Button { Tag = "power:" + text, Content = text, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            choice.Click += (_, _) => { _powerChoices.IsVisible = false; InvokeFooter(action); };
            _powerChoices.Children.Add(choice);
        }

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(who);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        return new Border
        {
            BorderBrush = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0, 4, 0, 0),
            Child = new StackPanel { Children = { _powerChoices, bar } },
        };
    }

    private static Control Badge(IBrush? picture, string initial, IBrush fill)
        => new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = picture ?? fill,
            Child = picture is null ? new TextBlock { Text = initial, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } : null,
        };

    private Button FooterButton(string name, string tip, IReadOnlyList<string> icons, LauncherFooterAction? action)
    {
        var button = new Button { Tag = "footer:" + name, Background = Brushes.Transparent, Padding = new Thickness(10), Content = new ShellIcon { IconNames = icons, IconSize = 20 } };
        ToolTip.SetTip(button, tip);
        if (action is { } a) button.Click += (_, _) => InvokeFooter(a);
        return button;
    }

    private void Launch(DesktopEntry app)
    {
        _model.RecordLaunch(app.Id);
        AppInvoked?.Invoke(this, app);
    }

    private Control Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 8, 0, 0) };

    // A heading with a small bordered action on its right — the "Pinned / All apps >" and
    // "Recommended / More >" rows of the Windows 11 start menu.
    private Control HeadingWith(string text, Control side)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Heading(text));
        Grid.SetColumn(side, 1);
        row.Children.Add(side);
        return row;
    }

    private Button SideLink(string text)
    {
        var button = new Button
        {
            Tag = text,
            Padding = new Thickness(10, 4),
            Background = Brushes.Transparent,
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = text, FontSize = 12 }, new TextBlock { Text = ">", FontSize = 12 } } },
        };
        button.Click += (_, _) => ShowAllApps = true;
        return button;
    }

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
