using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// Settings: Windows 11 style. A sidebar with the user's chip, a search box and the categories; on the
// right the chosen category as a big title over a list of entry cards (System also leads with the
// device card), or the search results; opening an item shows its page with a back row, or hands it to the shell.
public sealed class ControlPanelWindow : Window
{
    private readonly ControlPanelCatalog _catalog;
    private readonly IHive _hive;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>> _choices;
    private readonly Action<ControlPanelItem> _openExternal;
    private readonly Func<string, Control?>? _customPages;
    private readonly DesktopSettings? _deviceSettings;
    private readonly TextBox _search = new() { Watermark = "Find a setting" };
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 26, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _body = new();
    private readonly StackPanel _pageHead = new() { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false };
    private readonly StackPanel _pageHeadTitle = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageHeadCategory = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 };
    private readonly TextBlock _pageHeadSeparator = new() { Text = "›", FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5, IsVisible = false };
    private readonly TextBlock _pageHeadName = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<ControlPanelItem> _shown = [];
    private string _heading = "";
    private Control? _pageView;

    // openExternal starts items that are not pages of ours (a command, a component, a .desktop settings program).
    public ControlPanelWindow(ControlPanelCatalog catalog, IHive hive, Action<ControlPanelItem> openExternal,
        IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>>? choices = null, Func<string, Control?>? customPages = null,
        UserProfile? user = null, DesktopSettings? deviceSettings = null)
    {
        _customPages = customPages;
        _catalog = catalog;
        _hive = hive;
        _openExternal = openExternal;
        _choices = choices ?? new Dictionary<string, IReadOnlyList<SettingChoice>>();
        _deviceSettings = deviceSettings;
        Title = "Settings";
        Width = 1020; Height = 700;

        var categories = ControlPanelCategories.All.Where(c => catalog.InCategory(c).Count > 0).ToList();
        foreach (var c in categories)
            _nav.Children.Add(NavButton(c));

        var profile = user ?? new UserProfile();
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 0, 0, 10),
            Children = { Avatar(profile), new TextBlock { Text = profile.DisplayName, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } },
        };

        _search.Margin = new Thickness(0, 0, 0, 10);
        _search.TextChanged += (_, _) => Search(_search.Text ?? "");

        var side = new ScrollViewer
        {
            Margin = new Thickness(6),
            Content = new StackPanel { Children = { chip, _search, _nav } },
        };

        var back = new Button
        {
            Padding = new Thickness(8, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new TextBlock { Text = "<", FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTip.SetTip(back, "Back");
        back.Click += (_, _) =>
        {
            _pageView = null;
            _pageHead.IsVisible = false;
            _title.IsVisible = true;
            if (_search.Text?.Trim().Length > 0) Search(_search.Text);
            else if (_heading.Length > 0) Show(_shown, _heading);
        };
        _pageHead.Children.Add(back);
        _pageHeadTitle.Children.Add(_pageHeadCategory);
        _pageHeadTitle.Children.Add(_pageHeadSeparator);
        _pageHeadTitle.Children.Add(_pageHeadName);
        _pageHead.Children.Add(_pageHeadTitle);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(_pageHead);
        _title.SetValue(Grid.ColumnProperty, 1);
        _title.IsVisible = false;   // a page open on the right carries its own header
        header.Children.Add(_title);

        var content = new ScrollViewer
        {
            Margin = new Thickness(28, 22, 28, 16),
            Content = new StackPanel { Children = { header, _body } },
        };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*") };
        root.Children.Add(side);
        content.SetValue(Grid.ColumnProperty, 1);
        root.Children.Add(content);
        Content = root;

        if (_nav.Children.OfType<Button>().FirstOrDefault() is { } first)
            first.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    public IReadOnlyList<ControlPanelItem> ShownItems => _shown;
    public string? OpenPageId => (_pageView as SettingsPageView)?.PageId
        ?? (_pageView is DesktopPageView ? DesktopPageView.PageId : null)
        ?? (_pageView is DisplayPageView ? DisplayPageView.PageId : null);
    public SettingsPageView? Page => _pageView as SettingsPageView;
    // The breadcrumb of an open page ("Display" with category "System"), for hosts and tests.
    public string PageName => _pageHeadName.Text;
    public string? PageCategory => _pageHeadCategory.IsVisible ? _pageHeadCategory.Text : null;

    private Button NavButton(string category)
    {
        var button = new Button
        {
            Tag = category,
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new ShellIcon { IconNames = NavIcons(category), IconSize = 18 },
                    new TextBlock { Text = NavLabel(category), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) =>
        {
            foreach (var other in _nav.Children.OfType<Button>())
                other.Background = Equals(other.Tag, button.Tag)
                    ? ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.LightGray)
                    : Brushes.Transparent;
            if (_pageView is not null)   // re-clicking a category leaves the open page alone
                return;
            Show(_catalog.InCategory(category), NavLabel(category));
        };
        return button;
    }

    public void Search(string text)
    {
        if (text.Trim().Length > 0)
        {
            Show(_catalog.Search(text), "Search results");
            return;
        }
        if (_heading.Length > 0 && _heading != "Search results" && _pageView is null)
            Show(_catalog.InCategory(_heading), _heading);
    }

    // Our own pages show on the right; everything else is started by the shell.
    public void Open(ControlPanelItem item)
    {
        if (item.PageId is { } custom && _customPages?.Invoke(custom) is { } control)
            ShowPage(custom, control);
        else if (item.PageId is { } id && BuiltInPages.Find(id) is { } page)
            ShowPage(id, new SettingsPageView(page, _hive, _choices));
        else
            _openExternal(item);
    }

    private void ShowPage(string id, Control view)
    {
        _pageView = view;
        _pageHead.IsVisible = true;
        _title.IsVisible = false;
        // The page head is a breadcrumb: the category its item sits in, then the page ("System › Display").
        var item = _catalog.Items.FirstOrDefault(i => string.Equals(i.PageId, id, StringComparison.OrdinalIgnoreCase));
        var title = BuiltInPages.Find(id)?.Title ?? (id == DesktopPageView.PageId ? "Desktop" : id);
        _pageHeadName.Text = item is not null ? item.Name : title;
        _pageHeadCategory.Text = item?.Category ?? "";
        _pageHeadCategory.IsVisible = _pageHeadSeparator.IsVisible = item is not null && item.Category != item.Name;
        _body.Content = view;
    }

    private void Show(IReadOnlyList<ControlPanelItem> items, string heading)
    {
        _shown = items;
        _heading = heading;
        if (!_pageHead.IsVisible)   // a category's cards sit under its title
        {
            _title.Text = heading;
            _title.IsVisible = true;
        }
        var cards = new StackPanel { Spacing = 4 };
        if (!_pageHead.IsVisible && heading == NavLabel(ControlPanelCategories.System))
            cards.Children.Add(DeviceCard());
        if (items.Count == 0)
            cards.Children.Add(new TextBlock { Text = "No matching settings.", Opacity = 0.7, Margin = new Thickness(2, 8, 0, 0) });
        foreach (var item in items)
            cards.Children.Add(Card(item));
        _body.Content = cards;
    }

    private Control Card(ControlPanelItem item)
    {
        var item_ = item;
        var card = new Button
        {
            Tag = "card:" + item.Id,
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var icon = new ShellIcon { IconName = item.Icon, IconSize = 26, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel
        {
            Spacing = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = item.Name }, new TextBlock { Text = item.Description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap } },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        texts.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(texts);
        var chevron = new TextBlock { Text = ">", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(chevron);
        card.Content = grid;
        card.Click += (_, _) => Open(item_);
        return card;
    }

    // Windows 11's System page leads with the device card: the machine's name and the wallpaper as preview.
    private Control DeviceCard()
    {
        IBrush preview;
        if (_deviceSettings is { } settings)
        {
            try { preview = WallpaperBrush.Create(settings); }
            catch (Exception) { preview = DeviceGradient(); }
        }
        else preview = DeviceGradient();

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.WhiteSmoke),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 14,
                Children =
                {
                    new Border { Width = 110, Height = 74, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = preview },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = MachineName(), FontWeight = FontWeight.SemiBold, FontSize = 15 },
                            new TextBlock { Text = "Vibe DE", FontSize = 12, Opacity = 0.7 },
                        },
                    },
                },
            },
        };
    }

    private static LinearGradientBrush DeviceGradient() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#4babff"), 0), new GradientStop(Color.Parse("#205a9e"), 1) },
    };

    private static string MachineName()
    {
        try { var name = Environment.MachineName; return name.Length > 0 ? name : "This computer"; }
        catch (Exception) { return "This computer"; }
    }

    // Windows 11's round avatar over the sidebar; the initial on a disc when there is no picture.
    private static Control Avatar(UserProfile profile)
    {
        IBrush? fill = null;
        if (profile.PicturePath is { } picture && File.Exists(picture))
            try { fill = new ImageBrush(new Bitmap(picture)) { Stretch = Stretch.UniformToFill }; }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { }
        return new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21),
            Background = fill ?? new SolidColorBrush(Color.FromRgb(96, 96, 96)),
            Child = fill is null ? new TextBlock { Text = profile.Initial, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } : null,
        };
    }

    private static string NavLabel(string category) => category switch
    {
        ControlPanelCategories.Appearance => "Personalization",
        ControlPanelCategories.Privacy => "Privacy",
        _ => category,
    };

    private static IReadOnlyList<string> NavIcons(string category) => category switch
    {
        ControlPanelCategories.System => ["computer"],
        ControlPanelCategories.Appearance => ["preferences-desktop-theme", "preferences-desktop"],
        ControlPanelCategories.Devices => ["preferences-desktop-peripherals", "input-keyboard"],
        ControlPanelCategories.Network => ["preferences-system-network", "network-wireless"],
        ControlPanelCategories.Apps => ["applications-system"],
        ControlPanelCategories.Other => ["applications-other"],
        ControlPanelCategories.Privacy => ["security-high"],
        ControlPanelCategories.Accessibility => ["preferences-desktop-accessibility"],
        _ => ["applications-other"],
    };
}
