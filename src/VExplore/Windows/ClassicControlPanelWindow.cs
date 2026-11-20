using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// The classic Control Panel, the Windows 7 look: a toolbar with back, forward, up and refresh over an address
// box, a search field in the corner, and under the banner "Adjust your computer's settings" the categories as
// big teal links with their first items as sub-links. "View by" switches between category links and large
// icons. Our own settings pages open inside the window; everything else the shell starts.
public sealed class ClassicControlPanelWindow : Window
{
    private static readonly Color TitleAccent = Color.Parse("#0d7f8f");   // category titles
    private static readonly Color LinkAccent = Color.Parse("#2f6fc4");    // item links
    private static readonly Color HeaderFore = Color.Parse("#1e3a5f");    // headings

    private readonly ControlPanelCatalog _catalog;
    private readonly IHive _hive;
    private readonly Action<ControlPanelItem> _openExternal;
    private readonly Func<string, Control?>? _customPages;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>> _choices;
    private readonly TextBlock _breadcrumb = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Width = 220, PlaceholderText = "Search Control Panel" };
    private readonly ContentControl _content = new();
    private readonly TextBlock _viewByLabel = new() { Text = "Category", VerticalAlignment = VerticalAlignment.Center };
    private Button _viewBy = new();
    private readonly Stack<string> _backStack = new(), _forwardStack = new();
    private string _address = "home";
    private string _lastNonSearch = "home";
    private string _pageId = "", _pageCategory = "", _pageName = "";
    private Control? _pageControl;
    private IReadOnlyList<ControlPanelItem> _shown = [];

    // openExternal starts items that are not our pages (a command, a component, a .desktop settings program).
    public ClassicControlPanelWindow(ControlPanelCatalog catalog, IHive hive, Action<ControlPanelItem> openExternal,
        IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>>? choices = null, Func<string, Control?>? customPages = null)
    {
        _customPages = customPages;
        _catalog = catalog;
        _hive = hive;
        _openExternal = openExternal;
        _choices = choices ?? new Dictionary<string, IReadOnlyList<SettingChoice>>();
        Title = "Control Panel";
        Width = 900; Height = 600;

        var back = NavButton("go-previous");
        back.Click += (_, _) => GoBack();
        var forward = NavButton("go-next");
        forward.Click += (_, _) => GoForward();
        var up = NavButton("go-up");
        up.Click += (_, _) => Navigate("home");
        var refresh = NavButton("view-refresh");
        refresh.Click += (_, _) => ShowView();
        _back = back;
        _forward = forward;
        _up = up;
        _search.TextChanged += (_, _) => Search(_search.Text ?? "");

        var viewMenu = new MenuFlyout
        {
            Items =
            {
                MenuEntry("Category", () => Navigate("home")),
                MenuEntry("Large icons", () => Navigate("icons")),
            },
        };
        _viewBy = new Button
        {
            Padding = new Thickness(4, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Flyout = viewMenu,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { new TextBlock { Text = "View by:", VerticalAlignment = VerticalAlignment.Center }, _viewByLabel, new TextBlock { Text = "▾", VerticalAlignment = VerticalAlignment.Center } },
            },
        };

        // The address box shows where in the Control Panel we are; the search field sits apart on the right.
        var addressGroup = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new ShellIcon { IconName = "preferences-system", IconSize = 14, VerticalAlignment = VerticalAlignment.Center }, _breadcrumb },
            },
        };
        var addressBox = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = true, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right);
        addressBox.Children.Add(refresh);
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in new[] { back, forward, up })
            navButtons.Children.Add(button);
        DockPanel.SetDock(navButtons, Dock.Left);
        addressBox.Children.Add(navButtons);
        addressBox.Children.Add(addressGroup);

        var toolbar = new DockPanel { Margin = new Thickness(12, 8, 12, 6), LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Right);
        toolbar.Children.Add(_search);
        toolbar.Children.Add(addressBox);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        _content.SetValue(Grid.RowProperty, 1);
        root.Children.Add(_content);
        Content = root;

        ShowView();
    }

    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _up;

    public IReadOnlyList<string> Categories => VisibleCategories;

    // Every item currently shown with a link: the sub-links of the home page, the rows of a category or
    // search page, the tiles of the large icons view.
    public IReadOnlyList<ControlPanelItem> ShownItems => _shown;

    public string? OpenPageId => _address.StartsWith("page:") ? _pageId : null;

    public void Search(string text)
    {
        if (text.Trim().Length == 0)
        {
            if (_address.StartsWith("search:")) { _address = _lastNonSearch; ShowView(); }
            return;
        }
        _lastNonSearch = _address.StartsWith("search:") ? _lastNonSearch : _address;
        _address = "search:" + text.Trim();
        ShowView();   // keystrokes are navigation noise: no history entry
    }

    // Our own pages open in the window; everything else is started by the shell.
    public void Open(ControlPanelItem item)
    {
        if (item.PageId is { } id)
            _pageControl = _customPages?.Invoke(id) ?? (BuiltInPages.Find(id) is { } page ? new SettingsPageView(page, _hive, _choices) : null);
        else
            _pageControl = null;
        if (_pageControl is not null)
        {
            _pageId = item.PageId!;
            _pageCategory = item.Category;
            _pageName = item.Name;
            Navigate("page:" + _pageId);
            return;
        }
        _openExternal(item);
    }

    private void Navigate(string address)
    {
        if (address == _address) return;
        _backStack.Push(_address);
        _forwardStack.Clear();
        _address = address;
        if (!address.StartsWith("search:")) _lastNonSearch = address;
        ShowView();
    }

    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        _forwardStack.Push(_address);
        _address = _backStack.Pop();
        ShowView();
    }

    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(_address);
        _address = _forwardStack.Pop();
        ShowView();
    }

    private void ShowView()
    {
        _shown = _address switch
        {
            "home" => VisibleCategories.SelectMany(c => _catalog.InCategory(c).Take(3)).ToList(),
            "icons" => _catalog.Items,
            _ when _address.StartsWith("category:") => _catalog.InCategory(_address[9..]),
            _ when _address.StartsWith("search:") => _catalog.Search(_address[7..]),
            _ => [],
        };
        _content.Content = _address switch
        {
            "home" => HomePage(),
            "icons" => IconsPage(),
            _ when _address.StartsWith("category:") => ListPage(Heading(_address[9..]), _shown),
            _ when _address.StartsWith("search:") => ListPage("Search Results", _shown),
            _ when _address.StartsWith("page:") => PageView(),
            _ => (Control)new TextBlock(),
        };
        _breadcrumb.Text = _address switch
        {
            "icons" => "Control Panel",
            _ when _address.StartsWith("category:") => Trail("Control Panel", Heading(_address[9..])),
            _ when _address.StartsWith("search:") => Trail("Control Panel", "Search results"),
            _ when _address.StartsWith("page:") => Trail("Control Panel", Heading(_pageCategory), _pageName),
            _ => "Control Panel",
        };
        _viewByLabel.Text = _address == "icons" ? "Large icons" : "Category";
        _back.IsEnabled = _backStack.Count > 0;
        _forward.IsEnabled = _forwardStack.Count > 0;
        _up.IsEnabled = _address != "home";
    }

    // The home page: the banner line with the "View by" switch and the categories as blocks of a big link
    // and up to three sub-links.
    private Control HomePage()
    {
        var header = new DockPanel { Margin = new Thickness(8, 4, 8, 2), LastChildFill = false };
        var banner = new TextBlock { Text = "Adjust your computer's settings", FontSize = 21, Foreground = new SolidColorBrush(HeaderFore), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(banner, Dock.Left);
        header.Children.Add(banner);
        DockPanel.SetDock(_viewBy, Dock.Right);
        header.Children.Add(_viewBy);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(16, 6, 0, 0) };
        foreach (var category in VisibleCategories)
        {
            var links = new StackPanel();
            var here = category;
            links.Children.Add(Link(Heading(category), 15, TitleAccent, () => Navigate("category:" + here)));
            foreach (var item in _catalog.InCategory(category).Take(3))
            {
                var it = item;
                links.Children.Add(Link(it.Name, 12, LinkAccent, () => Open(it)));
            }
            var block = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 12, 40, 8) };
            block.Children.Add(new ShellIcon { IconName = CategoryIcon(category), IconSize = 44 });
            block.Children.Add(links);
            grid.Children.Add(block);
        }
        var page = new StackPanel();
        page.Children.Add(header);
        page.Children.Add(grid);
        return page;
    }

    // All items at once as tiles, the "Large icons" view.
    private Control IconsPage()
    {
        var flow = new WrapPanel { Margin = new Thickness(8, 8, 0, 0) };
        foreach (var item in _catalog.Items)
        {
            var it = item;
            var tile = Link(it.Name, 12, TitleAccent, () => Open(it));
            tile.Padding = new Thickness(8, 2);
            var block = new StackPanel { Spacing = 6, Width = 130, Margin = new Thickness(0, 0, 16, 10) };
            block.Children.Add(new ShellIcon { IconName = it.Icon, IconSize = 48, HorizontalAlignment = HorizontalAlignment.Center });
            block.Children.Add(tile);
            flow.Children.Add(block);
        }
        return new ScrollViewer { Content = flow };
    }

    // A list of items with their descriptions: a category page or search results.
    private Control ListPage(string heading, IReadOnlyList<ControlPanelItem> items)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = heading, FontSize = 20, Foreground = new SolidColorBrush(HeaderFore), Margin = new Thickness(8, 4, 8, 4) });
        var list = new StackPanel { Margin = new Thickness(16, 0, 8, 0) };
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = "No matching settings.", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var item in items)
        {
            var it = item;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 8) };
            row.Children.Add(new ShellIcon { IconName = it.Icon, IconSize = 34 });
            row.Children.Add(new StackPanel
            {
                Spacing = 1,
                Children = { Link(it.Name, 14, TitleAccent, () => Open(it)), new TextBlock { Text = it.Description, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap } },
            });
            list.Children.Add(row);
        }
        page.Children.Add(list);
        return new ScrollViewer { Content = page };
    }

    // A page of ours under its own title; the address bar carries the trail.
    private Control PageView() => new StackPanel
    {
        Children =
        {
            new TextBlock { Text = _pageName, FontSize = 20, Foreground = new SolidColorBrush(HeaderFore), Margin = new Thickness(8, 4, 8, 10) },
            _pageControl ?? new TextBlock { Text = "This page could not be loaded.", Opacity = 0.7 },
        },
    };

    private Button NavButton(string icon) => new()
    {
        Padding = new Thickness(6, 4),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Content = new ShellIcon { IconName = icon, IconSize = 16 },
    };

    private static MenuItem MenuEntry(string header, Action invoke)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => invoke();
        return item;
    }

    private Button Link(string text, double size, Color color, Action invoke)
    {
        var label = new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var button = new Button
        {
            Padding = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.PointerEntered += (_, _) => label.TextDecorations = TextDecorations.Underline;
        button.PointerExited += (_, _) => label.TextDecorations = null;
        button.Click += (_, _) => invoke();
        return button;
    }

    private static string Trail(params string[] parts) => string.Join("  ›  ", parts.Where(p => p.Length > 0));

    private static string Heading(string category) => category;

    private static string CategoryIcon(string category) => category switch
    {
        ControlPanelCategories.System => "computer",
        ControlPanelCategories.Appearance => "preferences-desktop-theme",
        ControlPanelCategories.Devices => "preferences-desktop-peripherals",
        ControlPanelCategories.Network => "preferences-system-network",
        ControlPanelCategories.Apps => "applications-system",
        ControlPanelCategories.Other => "applications-other",
        ControlPanelCategories.Privacy => "security-high",
        ControlPanelCategories.Accessibility => "preferences-desktop-accessibility",
        _ => "applications-other",
    };

    private IReadOnlyList<string> VisibleCategories => ControlPanelCategories.All.Where(c => _catalog.InCategory(c).Count > 0).ToList();
}
