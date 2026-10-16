using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vde.Shell;
using Vde.Shell.Controls;
using Vibe.Registry;

namespace VExplore.Windows;

// Settings: a search box and the categories on the left, the items of the chosen category (or the search
// results) in the middle, and a page on the right when an item has one of its own.
public sealed class ControlPanelWindow : Window
{
    private readonly ControlPanelCatalog _catalog;
    private readonly IHive _hive;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>> _choices;
    private readonly Action<ControlPanelItem> _openExternal;
    private readonly Func<string, Control?>? _customPages;
    private readonly TextBox _search = new() { Watermark = "Find a setting" };
    private readonly ListBox _categories = new();
    private readonly ListBox _items = new();
    private readonly ContentControl _page = new();
    private IReadOnlyList<ControlPanelItem> _shown = [];

    // openExternal starts items that are not pages of ours (a command, a component, a .desktop settings program).
    public ControlPanelWindow(ControlPanelCatalog catalog, IHive hive, Action<ControlPanelItem> openExternal,
        IReadOnlyDictionary<string, IReadOnlyList<SettingChoice>>? choices = null, Func<string, Control?>? customPages = null)
    {
        _customPages = customPages;
        _catalog = catalog;
        _hive = hive;
        _openExternal = openExternal;
        _choices = choices ?? new Dictionary<string, IReadOnlyList<SettingChoice>>();
        Title = "Settings";
        Width = 960; Height = 640;

        _categories.ItemsSource = ControlPanelCategories.All.Where(c => catalog.InCategory(c).Count > 0).ToList();
        _categories.SelectionChanged += (_, _) => { if (_categories.SelectedItem is string c) Show(catalog.InCategory(c)); };
        _search.TextChanged += (_, _) => Search(_search.Text ?? "");
        _items.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ControlPanelItem>((item, _) =>
            item is null ? null : new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new ShellIcon { IconName = item.Icon, IconSize = 24 },
                    new StackPanel { Children = { new TextBlock { Text = item.Name }, new TextBlock { Text = item.Description, FontSize = 12, Opacity = 0.7 } } },
                },
            });
        _items.SelectionChanged += (_, _) => { if (_items.SelectedItem is ControlPanelItem item) Open(item); };

        var left = new DockPanel { Width = 190 };
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        left.Children.Add(_categories);
        var middle = new Border { Width = 320, Child = _items };

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(middle, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(middle);
        root.Children.Add(_page);
        Content = root;

        if (_categories.Items.Count > 0)
            _categories.SelectedIndex = 0;
    }

    public IReadOnlyList<ControlPanelItem> ShownItems => _shown;
    public string? OpenPageId => (_page.Content as SettingsPageView)?.PageId ?? (_page.Content is DesktopPageView ? DesktopPageView.PageId : null);
    public SettingsPageView? Page => _page.Content as SettingsPageView;

    public void Search(string text)
    {
        if (text.Trim().Length == 0)
        {
            if (_categories.SelectedItem is string c) Show(_catalog.InCategory(c));
            return;
        }
        Show(_catalog.Search(text));
    }

    // Our own pages show on the right; everything else is started by the shell.
    public void Open(ControlPanelItem item)
    {
        if (item.PageId is { } custom && _customPages?.Invoke(custom) is { } control)
            _page.Content = control;   // a page that is more than a list of settings
        else if (item.PageId is { } id && BuiltInPages.Find(id) is { } page)
            _page.Content = new SettingsPageView(page, _hive, _choices);
        else
            _openExternal(item);
    }

    private void Show(IReadOnlyList<ControlPanelItem> items)
    {
        _shown = items;
        _items.ItemsSource = items;
    }
}
