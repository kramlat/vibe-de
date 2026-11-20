using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Components;
using Vde.Shell;
using Vde.Shell.ComDlg;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace VExplore.Windows;

// A folder window in the browser style: the titlebar is a strip of tabs, one folder view per tab, with a
// new-tab button after them and minimize / maximize / close at the right end. The window draws its own
// chrome, because the compositor's titlebar cannot host tabs; the strip drags the window, its empty areas
// open a window menu, and invisible strips along the edges resize. The old single-folder API forwards to
// the active tab's view.
public sealed class FileManagerWindow : Window
{
    private const int BarHeight = 36;
    private const int EdgeWidth = 5;

    private readonly KnownFolders _folders;
    private readonly Action<string> _openFile;
    private readonly FileTypes? _types;
    private readonly FileActions? _actions;
    private readonly IFileClipboard? _clipboard;
    private readonly ShortcutStyleService? _shortcuts;
    private readonly Func<IReadOnlyList<string>, PropertySheetContext>? _properties;
    private readonly ShellFolderCatalog? _shellFolders;

    private readonly Grid _pages = new();
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly List<Tab> _tabs = [];
    private Tab _active = null!;

    public FileManagerWindow(string startFolder, KnownFolders folders, Action<string> openFile, FileTypes? types = null,
        FileActions? actions = null, IFileClipboard? clipboard = null, ShortcutStyleService? shortcuts = null,
        Func<IReadOnlyList<string>, PropertySheetContext>? properties = null, ShellFolderCatalog? shellFolders = null,
        DesktopSettings? settings = null)
    {
        _folders = folders;
        _openFile = openFile;
        _types = types;
        _actions = actions;
        _clipboard = clipboard;
        _shortcuts = shortcuts;
        _properties = properties;
        _shellFolders = shellFolders;

        Width = 960; Height = 600;
        // The compositor draws a titlebar only for windows that ask for one; this window wants tabs in the
        // titlebar, so the chrome here is the window's own, on the transparent rounded body every shell
        // window in Windows 11 mode uses.
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        var body = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = Brushes.White,
            Child = Root(),
        };
        Opened += (_, _) => body.Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.White);
        WindowMica.Attach(this, settings, body);   // subscribed after the surface colour, so it paints over it
        Content = body;

        NewTab(startFolder);
        Opened += (_, _) => StartupTrace.Mark("window opened");
        EventHandler? first = null;
        first = (_, _) => { LayoutUpdated -= first; StartupTrace.Mark("first layout done"); StartupTrace.ShotIfAsked(this); StartupTrace.ExitIfAsked(); };
        LayoutUpdated += first;
    }

    // The titlebar strip above the folder pages, with the resize edges over everything.
    private Grid Root()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(BarHeight + ",*") };

        var bar = new Border { Child = Titlebar(), Background = Brushes.Transparent };
        Grid.SetRow(bar, 0);
        bar.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed) { WindowMenu(); return; }
            if (point.Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else if (WindowState != WindowState.Maximized) BeginMoveDrag(e);
                e.Handled = true;
            }
        };

        Grid.SetRow(_pages, 1);
        grid.Children.Add(bar);
        grid.Children.Add(_pages);
        foreach (var edge in Edges())
            grid.Children.Add(edge);
        return grid;
    }

    // One per tab: the folder view and its header in the strip.
    private sealed class Tab
    {
        public required FileManagerView View;
        public required Border Header;
        public required TextBlock Label;
        public required ShellIcon Icon;
    }

    private Grid Titlebar()
    {
        var plus = new Button
        {
            Content = new TextBlock { Text = "+", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(9, 0), Height = BarHeight - 4,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 2, 0),
        };
        ToolTip.SetTip(plus, "New tab (Ctrl+T)");
        plus.Click += (_, _) => NewTab(_active.View.Home);

        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        windowButtons.Children.Add(WinButton("\u2014", "Minimize", () => WindowState = WindowState.Minimized));
        windowButtons.Children.Add(WinButton("\u25a1", "Maximize", ToggleMaximize));
        windowButtons.Children.Add(WinButton("\u2715", "Close", Close, danger: true));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var tabs = new DockPanel { LastChildFill = true, Children = { _strip, plus } };   // _strip docked left, + after the tabs
        DockPanel.SetDock(_strip, Dock.Left);
        Grid.SetColumn(tabs, 0);
        grid.Children.Add(tabs);
        Grid.SetColumn(windowButtons, 1);
        grid.Children.Add(windowButtons);
        return grid;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void WindowMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string label, Action invoke)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => invoke();
            return item;
        }
        menu.Items.Add(Item("New tab", () => NewTab(_active.View.Home)));
        menu.Items.Add(Item("Minimize", () => WindowState = WindowState.Minimized));
        menu.Items.Add(Item(WindowState == WindowState.Maximized ? "Restore" : "Maximize", ToggleMaximize));
        menu.Items.Add(Item("Close", Close));
        menu.Open(this);
    }

    // A chrome button with the fill only under the pointer; Close gets the danger colour, as in Explorer.
    private Button WinButton(string glyph, string tip, Action invoke, bool danger = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            Width = 44, Height = 30, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => invoke();
        button.PointerEntered += (_, _) =>
        {
            var fill = danger ? ThemeTokens.Find(this, ThemeToken.Critical, Brushes.IndianRed) : ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.LightGray);
            if (fill is ISolidColorBrush solid)
                button.Background = new SolidColorBrush(solid.Color, 0.9);
        };
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    // Invisible strips along the window edges that start the compositor's resize grip (client-side windows
    // get none from vwm).
    private IEnumerable<Border> Edges()
    {
        (WindowEdge edge, double width, double height, StandardCursorType cursor, int row, int rowSpan)[] specs =
        [
            (WindowEdge.North, double.NaN, double.NaN, StandardCursorType.SizeNorthSouth, 0, 1),
            (WindowEdge.South, double.NaN, double.NaN, StandardCursorType.SizeNorthSouth, 1, 1),
            (WindowEdge.West, double.NaN, double.NaN, StandardCursorType.SizeWestEast, 0, 2),
            (WindowEdge.East, double.NaN, double.NaN, StandardCursorType.SizeWestEast, 0, 2),
        ];
        foreach (var (edge, _, _, cursor, row, rowSpan) in specs)
        {
            var border = new Border
            {
                Width = edge is WindowEdge.West or WindowEdge.East ? EdgeWidth : double.NaN,
                Height = edge is WindowEdge.North or WindowEdge.South ? EdgeWidth : double.NaN,
                HorizontalAlignment = edge switch { WindowEdge.West => HorizontalAlignment.Left, WindowEdge.East => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch },
                VerticalAlignment = edge switch { WindowEdge.North => VerticalAlignment.Top, WindowEdge.South => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch },
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursor),
            };
            Grid.SetRow(border, row);
            Grid.SetRowSpan(border, rowSpan);
            border.PointerPressed += (_, e) =>
            {
                if (WindowState == WindowState.Maximized) return;
                BeginResizeDrag(edge, e);
                e.Handled = true;
            };
            yield return border;
        }
    }

    // ---- tabs ----

    public FileManagerView ActiveView => _active.View;
    public IReadOnlyList<FileManagerView> Views => _tabs.Select(t => t.View).ToList();

    public void NewTab(string? folder = null)   // a new tab starts in the given folder, else the active tab's home
    {
        var view = new FileManagerView(folder ?? _active.View.Home, _folders, _openFile, _types, _actions, _clipboard, _shortcuts, _properties, _shellFolders);
        // the path, cut at its start when it is long: the folder's own name at the end is what matters
        var label = new TextBlock { TextTrimming = TextTrimming.PrefixCharacterEllipsis, MaxWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        var icon = new ShellIcon { IconSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        var close = new Button
        {
            Content = new TextBlock { Text = "\u2715", FontSize = 9, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(10, 0, 4, 0),
            Height = BarHeight,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Child = new DockPanel { LastChildFill = true, Children = { close, icon, label } },
        };

        var tab = new Tab { View = view, Header = header, Label = label, Icon = icon };
        DockPanel.SetDock(close, Dock.Right);
        close.PointerPressed += (_, e) => e.Handled = true;   // the header must not see it as a tab switch
        close.Click += (_, _) => CloseTab(tab);
        header.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsMiddleButtonPressed) { CloseTab(tab); return; }
            if (point.Properties.IsLeftButtonPressed) SelectTab(tab);
            e.Handled = true;   // a tab press switches (or closes); it must not also drag the window
        };

        view.TitleChanged += (_, _) => TabTitleChanged(tab);
        label.Text = view.FolderPath;
        icon.IconNames = view.FolderIconNames;

        _pages.Children.Add(view);
        Grid.SetRow(view, 0);
        _strip.Children.Add(header);
        _tabs.Add(tab);
        SelectTab(tab);
    }

    // The tab header and the window title follow the folder the tab shows.
    private void TabTitleChanged(Tab tab)
    {
        tab.Label.Text = tab.View.FolderPath;
        tab.Icon.IconNames = tab.View.FolderIconNames;
        ToolTip.SetTip(tab.Header, tab.View.FolderPath);
        if (tab == _active)
            ShowFolder(tab);
    }

    // The window is named after the path of the folder it shows, and wears that folder's icon.
    private void ShowFolder(Tab tab)
    {
        Title = tab.View.FolderPath;
        try
        {
            if (ShellIconProvider.Default.Load(tab.View.FolderIconNames, 32, 1) is Avalonia.Media.Imaging.Bitmap bitmap)
                Icon = new WindowIcon(bitmap);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // a platform that cannot take a window icon keeps the program's
        }
    }

    private void SelectTab(Tab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            t.View.IsVisible = t == tab;
            t.Header.Background = t == tab ? ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.LightGray) : Brushes.Transparent;
        }
        ShowFolder(tab);
    }

    public void CloseTab(FileManagerView view)
    {
        var tab = _tabs.FirstOrDefault(t => t.View == view);
        if (tab is not null)
            CloseTab(tab);
    }

    private void CloseTab(Tab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.Remove(tab);
        tab.View.Dispose();
        _pages.Children.Remove(tab.View);
        _strip.Children.Remove(tab.Header);
        if (_tabs.Count == 0) { Close(); return; }
        if (tab == _active)
            SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    public void CycleTab(int step)
    {
        if (_tabs.Count < 2) return;
        SelectTab(_tabs[((_tabs.IndexOf(_active) + step) % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    // Ctrl+T a new tab, Ctrl+W closes this one, Ctrl+Tab moves on; every other key is the active tab's
    // shortcut. The keys are dispatched here because nothing inside may be focused to bubble through.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _active is null)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Action? invoke = e.Key switch
            {
                Key.T => () => NewTab(_active.View.Home),
                Key.W => () => CloseTab(_active),
                Key.Tab => () => CycleTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1),
                _ => null,
            };
            if (invoke is not null)
            {
                invoke();
                e.Handled = true;
                return;
            }
        }
        _active.View.HandleKey(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs)
            tab.View.Dispose();
        base.OnClosed(e);
    }

    // ---- everything below forwards to the active tab ----

    public string Home => _active.View.Home;
    public ShortcutStyle ShortcutStyle => _active.View.ShortcutStyle;
    public string BackHint => _active.View.BackHint;
    public string CurrentFolder => _active.View.CurrentFolder;
    public bool CanGoBack => _active.View.CanGoBack;
    public bool CanGoForward => _active.View.CanGoForward;
    public IReadOnlyList<FileEntry> Entries => _active.View.Entries;
    public string StatusText => _active.View.StatusText;
    public string? Notice => _active.View.Notice;
    public bool ShowHidden { get => _active.View.ShowHidden; set => _active.View.ShowHidden = value; }
    public bool InShell => _active.View.InShell;
    public string? ShellFolderName => _active.View.ShellFolderName;
    public string SearchText
    {
        get => _active.View.SearchText;
        set => _active.View.SearchText = value;
    }

    public void SelectEntries(params string[] names) => _active.View.SelectEntries(names);
    public IReadOnlyList<string> SelectedPaths => _active.View.SelectedPaths;
    public Task CopyAsync() => _active.View.CopyAsync();
    public Task CutAsync() => _active.View.CutAsync();
    public Task PasteAsync() => _active.View.PasteAsync();
    public void DeleteSelected(bool permanent = false) => _active.View.DeleteSelected(permanent);
    public string? NewFolder() => _active.View.NewFolder();
    public bool Rename(string newName) => _active.View.Rename(newName);
    public Task BeginRenameAsync() => _active.View.BeginRenameAsync();
    public Task DeletePermanentlyAsync() => _active.View.DeletePermanentlyAsync();
    public IReadOnlyList<string> HandleDrop(IReadOnlyList<string> sources, string? targetFolder, bool ctrl, bool shift)
        => _active.View.HandleDrop(sources, targetFolder, ctrl, shift);
    public PropertySheetWindow? ShowProperties() => _active.View.ShowProperties();
    public IReadOnlyList<ShellAction> ShellActions => _active.View.ShellActions;
    public Task InvokeShellActionAsync(string actionId) => _active.View.InvokeShellActionAsync(actionId);
    public void Navigate(string path) => _active.View.Navigate(path);
    public void GoBack() => _active.View.GoBack();
    public void GoForward() => _active.View.GoForward();
    public void GoUp() => _active.View.GoUp();
    public void Open(IReadOnlyList<FileEntry> items) => _active.View.Open(items);
    public void Reload() => _active.View.Reload();
}
