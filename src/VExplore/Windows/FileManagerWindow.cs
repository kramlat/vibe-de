using Vde.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.ComDlg;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// A folder window: places on the left, address bar and buttons on top, the folder's files in the middle and a
// status line below. It keeps itself current by watching the folder. Opening a file is delegated (the shell
// provides ShellExecute); this window only decides what a click means.
public sealed class FileManagerWindow : Window
{
    private readonly Action<string> _openFile;
    private readonly NavigationHistory _history;
    private readonly AddressBar _address = new();
    private readonly FileListView _list = new();
    private readonly PlacesSidebar _places = new();
    private readonly TextBox _search = new() { MinWidth = 210 };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4) };
    private readonly Button _back, _forward, _up, _refresh;
    private IReadOnlyList<FileEntry> _unfiltered = [];
    private bool _hidden;
    private FolderWatcher? _watcher;

    private readonly FileActions _actions;
    private readonly ShortcutStyleService _shortcuts;
    private readonly Func<IReadOnlyList<string>, PropertySheetContext>? _properties;
    private readonly ShellFolderCatalog? _shell;
    private IShellFolder? _openShellFolder;
    private readonly IFileClipboard _clipboard;

    public FileManagerWindow(string startFolder, KnownFolders folders, Action<string> openFile, FileTypes? types = null,
        FileActions? actions = null, IFileClipboard? clipboard = null, ShortcutStyleService? shortcuts = null,
        Func<IReadOnlyList<string>, PropertySheetContext>? properties = null, ShellFolderCatalog? shellFolders = null)
    {
        _properties = properties;
        _shell = shellFolders;
        _openFile = openFile;
        _shortcuts = shortcuts ?? new ShortcutStyleService(new Hive("shortcuts"));
        _shortcuts.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyShortcutStyle);
        _actions = actions ?? new FileActions(new Trash());
        _clipboard = clipboard ?? new AvaloniaFileClipboard(() => GetTopLevel(this));
        Home = folders.Get(KnownFolder.Home);
        _history = new NavigationHistory(startFolder);
        Width = 960; Height = 600;

        _list.Types = types;
        _address.Home = Home;
        _places.Places = Places.Default(folders, shellFolders);

        _back = NavButton(["go-previous"]);
        _forward = NavButton(["go-next"]);
        _up = NavButton(["go-up"]);
        _refresh = NavButton(["view-refresh"]);
        _back.Click += (_, _) => Go(_history.Back(), record: false);
        _forward.Click += (_, _) => Go(_history.Forward(), record: false);
        _up.Click += (_, _) => Go(_history.Up(), record: false);
        _refresh.Click += (_, _) => Reload();
        _search.Watermark = "Search";
        _search.TextChanged += (_, _) => ApplyFilter();
        _address.PathRequested += (_, path) => Navigate(path);
        _places.PlaceInvoked += (_, place) =>
        {
            if (place.Path is not null)
                Navigate(place.Path);   // a folder, or "shell:..." for a shell folder
            else
                _status.Text = place.Name + " is not available here.";
        };
        _list.ItemsInvoked += (_, items) => Open(items);
        _list.SortChanged += (_, _) => Reload();
        _list.SelectionChanged += (_, _) => UpdateStatus();
        var menu = new ContextMenu();
        menu.Opening += (_, _) => FillContextMenu(menu);
        _list.InnerList.ContextMenu = menu;
        KeyDown += OnKeyDown;
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        _list.InnerList.PointerPressed += (_, e) =>
        {
            _dragStart = e.GetPosition(_list);
            _dragTrigger = e;
        };
        _list.InnerList.PointerMoved += OnPointerMoved;

        // The Windows 11 Explorer top area: navigation and a rounded address field with a search box on the
        // right, and below it a command bar of the common file actions.
        var navRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_search, Dock.Right);
        navRow.Children.Add(_search);
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in new[] { _back, _forward, _up, _refresh })
            navButtons.Children.Add(button);
        DockPanel.SetDock(navButtons, Dock.Left);
        navRow.Children.Add(navButtons);
        navRow.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3),
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _address,
        });

        var top = new StackPanel { Margin = new Thickness(8, 6, 8, 0), Children = { navRow, CommandBar() } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_places, Dock.Left);
        _places.Width = 190;
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_places);
        root.Children.Add(_list);
        Content = root;

        ApplyShortcutStyle();
        Reload();
    }

    public string Home { get; }
    public ShortcutStyle ShortcutStyle => _shortcuts.Style;
    public string BackHint => Hint(_back);

    private static string Hint(Button b) => ToolTip.GetTip(b) as string ?? "";

    // Button tooltips and Enter's meaning follow the shortcut style (Mac symbols only with an Apple keyboard).
    private void ApplyShortcutStyle()
    {
        var s = _shortcuts.Style;
        ToolTip.SetTip(_back, "Back (" + ShortcutMap.Label(ShortcutAction.Back, s) + ")");
        ToolTip.SetTip(_forward, "Forward (" + ShortcutMap.Label(ShortcutAction.Forward, s) + ")");
        ToolTip.SetTip(_up, "Up (" + ShortcutMap.Label(ShortcutAction.Up, s) + ")");
        ToolTip.SetTip(_refresh, "Refresh (" + ShortcutMap.Label(ShortcutAction.Refresh, s) + ")");
        _list.EnterOpens = s == ShortcutStyle.Pc;
    }

    public string CurrentFolder => _history.Current;
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    public IReadOnlyList<FileEntry> Entries => _list.Entries;
    public string StatusText => _status.Text ?? "";
    public bool ShowHidden { get => _hidden; set { _hidden = value; Reload(); } }
    public string SearchText
    {
        get => _search.Text ?? "";
        set { _search.Text = value; ApplyFilter(); }   // now: TextChanged may come later than the caller expects
    }

    // A flat Windows 11 style button showing a themed icon.
    private static Button NavButton(IReadOnlyList<string> icons, Action? invoke = null)
    {
        var button = new Button
        {
            Padding = new Thickness(10, 7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = new ShellIcon { IconNames = icons, IconSize = 16 },
        };
        if (invoke is not null) button.Click += (_, _) => invoke();
        return button;
    }

    // The command bar row: New, the clipboard and naming actions, then Sort and View folded into menus.
    private Control CommandBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(0, 6, 0, 2) };
        var newButton = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        newButton.Children.Add(new ShellIcon { IconNames = ["folder-new"], IconSize = 16 });
        newButton.Children.Add(new TextBlock { Text = "New", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(FlatButton(newButton, "New folder", () => NewFolder()));
        bar.Children.Add(Gap(bar));
        bar.Children.Add(FlatIconButton(["edit-cut"], "Cut", () => _ = CutAsync()));
        bar.Children.Add(FlatIconButton(["edit-copy"], "Copy", () => _ = CopyAsync()));
        bar.Children.Add(FlatIconButton(["edit-paste"], "Paste", () => _ = PasteAsync()));
        bar.Children.Add(FlatIconButton(["edit-rename", "document-edit"], "Rename", () => _ = BeginRenameAsync()));
        bar.Children.Add(FlatIconButton(["edit-delete"], "Delete", () => DeleteSelected()));
        bar.Children.Add(Gap(bar));

        MenuItem SortItem(string label, SortKey key)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _list.Sort(key, _list.SortKey == key && !_list.SortDescending);   // the same choice again flips the direction
            return item;
        }
        var sort = new ContextMenu
        {
            Items = { SortItem("Name", SortKey.Name), SortItem("Date modified", SortKey.Modified), SortItem("Type", SortKey.Type), SortItem("Size", SortKey.Size) },
        };
        bar.Children.Add(FoldButton("Sort", sort));

        var view = new ContextMenu();
        var hidden = new MenuItem { Header = "Hidden items" };
        hidden.Click += (_, _) => ShowHidden = !ShowHidden;
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Reload();
        view.Items.Add(hidden);
        view.Items.Add(refresh);
        bar.Children.Add(FoldButton("View", view));
        return bar;
    }

    private static Button FlatButton(object content, string tip, Action invoke)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(10, 7), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => invoke();
        return button;
    }

    private static Button FlatIconButton(IReadOnlyList<string> icons, string tip, Action invoke)
        => FlatButton(new ShellIcon { IconNames = icons, IconSize = 16 }, tip, invoke);

    // A labelled button with a chevron that opens the menu it folds (Sort, View).
    private Button FoldButton(string label, ContextMenu menu)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "⌄", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        var button = FlatButton(panel, label, () => { });
        button.Click += (_, e) => { menu.Placement = PlacementMode.Bottom; menu.Open(button); e.Handled = true; };
        return button;
    }

    private static Control Gap(Control relative) => new Border
    {
        Width = 1, Margin = new Thickness(7, 8),
        Background = ThemeTokens.Find(relative, ThemeToken.Border, Brushes.LightGray),
    };

    // The list narrowed to entries whose name contains the search text (case-insensitive).
    private bool FilterActive => SearchText.Trim().Length > 0;

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        _list.Entries = query.Length == 0
            ? _unfiltered
            : _unfiltered.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        UpdateStatus();
    }

    // The status line: how many entries, and how many are selected (only while it is showing counts).
    private void UpdateStatus()
    {
        var count = _list.Entries.Count;
        var selected = _list.SelectedEntries.Count;
        _status.Text = (count == 1 ? "1 item" : count + " items") + (selected > 0 ? " | " + (selected == 1 ? "1 selected" : selected + " selected") : "");
    }

    // ---- file actions: the same ones the desktop has ----

    private Point? _dragStart;
    private PointerPressedEventArgs? _dragTrigger;
    public string? Notice => StatusText;

    public void SelectEntries(params string[] names) => _list.Select(names);
    public IReadOnlyList<string> SelectedPaths
        => _list.SelectedEntries.Select(e => e.Tag is ShellItem item ? item.FileSystemPath : e.FullPath).Where(p => p is not null).Select(p => p!).ToList();

    public Task CopyAsync() => SetClipboardAsync(TransferKind.Copy);
    public Task CutAsync() => SetClipboardAsync(TransferKind.Cut);

    private async Task SetClipboardAsync(TransferKind kind)
    {
        if (SelectedPaths.Count > 0)
            await _clipboard.SetAsync(new FileTransfer(kind, SelectedPaths));
    }

    // Pastes into the current folder. Existing names are never overwritten: the copy gets "name (copy)".
    public async Task PasteAsync()
    {
        if (InShell) { _status.Text = "Nothing can be pasted here."; return; }
        if (await _clipboard.GetAsync() is not { } transfer)
            return;
        try
        {
            _actions.Paste(transfer, CurrentFolder, KeepBoth());
            if (transfer.Kind == TransferKind.Cut)
                await _clipboard.SetAsync(null);   // what was moved is not on the clipboard any more
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        Reload();
    }

    public void DeleteSelected(bool permanent = false)
    {
        if (InShell) { _ = InvokeShellActionAsync("delete"); return; }   // in the Trash, Delete means delete permanently
        try { _actions.Delete(SelectedPaths, permanent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        Reload();
    }

    public string? NewFolder()
    {
        if (InShell) { _status.Text = "Folders cannot be made here."; return null; }
        try
        {
            var path = _actions.NewFolder(CurrentFolder);
            Reload();
            _list.Select(Path.GetFileName(path));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; return null; }
    }

    public bool Rename(string newName)
    {
        var selected = SelectedPaths;
        var problem = selected.Count != 1 ? "Select one item to rename." : FileDialogRules.NameProblem(newName);
        if (problem is not null) { _status.Text = problem; return false; }
        try { _actions.Rename(selected[0], newName); Reload(); _list.Select(newName); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; return false; }
    }

    // F2: asks for the new name in a dialog.
    public async Task BeginRenameAsync()
    {
        if (SelectedPaths.Count != 1)
            return;
        var current = Path.GetFileName(SelectedPaths[0]);
        var name = await CommonDialogs.InputAsync(this, "New name:", "Rename", current, FileDialogRules.NameProblem);
        if (name is not null && name != current)
            Rename(name);
    }

    // Shift+Delete asks first; the files do not go to the trash.
    public async Task DeletePermanentlyAsync()
    {
        if (SelectedPaths.Count == 0) return;
        var answer = await CommonDialogs.MessageBoxAsync(this, $"Permanently delete {SelectedPaths.Count} item(s)? This cannot be undone.", "Delete", MessageButtons.YesNo, MessageIcon.Warning, DialogResult.No);
        if (answer == DialogResult.Yes) DeleteSelected(permanent: true);
    }

    // Files dropped on this window (from here or from another one): into the current folder, or into the folder row they were dropped on.
    public IReadOnlyList<string> HandleDrop(IReadOnlyList<string> sources, string? targetFolder, bool ctrl, bool shift)
    {
        if (InShell && targetFolder is null) return [];
        var target = targetFolder ?? CurrentFolder;
        var effect = DropRules.Decide(sources, target, ctrl, shift);
        if (effect == DropEffect.None) return [];
        try { return _actions.Drop(sources, target, effect, KeepBoth()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; return []; }
        finally { Reload(); }
    }

    private static FileOperationOptions KeepBoth() => new() { OnConflict = (_, _) => ConflictAction.KeepBoth };

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var sources = DroppedPaths(e);
        var effect = sources.Count == 0 ? DropEffect.None : DropRules.Decide(sources, CurrentFolder, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.DragEffects = effect switch { DropEffect.Copy => DragDropEffects.Copy, DropEffect.Move => DragDropEffects.Move, DropEffect.Link => DragDropEffects.Link, _ => DragDropEffects.None };
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HandleDrop(DroppedPaths(e), null, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private static IReadOnlyList<string> DroppedPaths(DragEventArgs e)
    {
        var formats = new Dictionary<string, byte[]>();
        foreach (var item in e.DataTransfer.Items.Select(i => (IDataTransferItem)i))
            foreach (var format in new[] { FileTransfer.UriList, FileTransfer.GnomeCopiedFiles })
                if (item.Contains(DataFormat.CreateBytesPlatformFormat(format)))
                    formats[format] = item.TryGetValue(DataFormat.CreateBytesPlatformFormat(format));
        return FileTransfer.FromFormats(formats)?.Paths ?? [];
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed || SelectedPaths.Count == 0)
            return;
        var now = e.GetPosition(_list);
        if (Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) < 12)
            return;   // a click, not a drag
        _dragStart = null;
        var data = new DataTransfer();
        foreach (var (format, bytes) in new FileTransfer(TransferKind.Copy, SelectedPaths).ToFormats())
            data.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(format), bytes));
        if (_dragTrigger is { } trigger)
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    // The Properties window for the selection (or for this folder when nothing is selected). Not modal, as in Explorer.
    public PropertySheetWindow? ShowProperties()
    {
        var paths = SelectedPaths.Count > 0 ? SelectedPaths : [CurrentFolder];
        var context = _properties?.Invoke(paths) ?? new PropertySheetContext { Target = new PropertyTarget(paths, ""), Actions = _actions, BuiltInProviders = [(PropertyPages.Clsid, new PropertyPages())] };
        try
        {
            var sheet = new PropertySheetWindow(context);
            sheet.Show();
            return sheet;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { _status.Text = e.Message; return null; }
    }

    // ---- shell folders (This Computer, Network, Trash, anything a component registers) ----

    public bool InShell => AddressPath.IsShellPath(_history.Current);
    public string? ShellFolderName => _openShellFolder?.DisplayName;

    private void ReloadShell()
    {
        _list.PreserveOrder = true;
        _address.Path = _history.Current;
        _places.ShowCurrent(_history.Current);
        _back.IsEnabled = _history.CanGoBack;
        _forward.IsEnabled = _history.CanGoForward;
        _up.IsEnabled = false;

        var info = _shell?.Resolve(_history.Current);
        if (info is null)
        {
            Title = "Not available";
            _unfiltered = [];
            _list.Entries = [];
            _status.Text = "This place is not available.";
            return;
        }
        try
        {
            var folder = _shell!.Open(info);
            folder.Changed += OnShellChanged;
            _openShellFolder = folder;
            Title = folder.DisplayName;
            var items = folder.GetItems();
            var path = _history.Current;
            _unfiltered = items.Select(i => new FileEntry(i.Name, path + "/" + i.Id, i.IsFolder, false, false, i.Size ?? 0, i.ModifiedUtc ?? default, "")
                { IconNames = i.IconNames, TypeText = i.TypeText, Detail = i.Detail, Tag = i }).ToList();
            ApplyFilter();
            _search.Watermark = "Search " + Title;
        }
        catch (Exception e) when (e is ComponentLoadException or InvalidOperationException or IOException)
        {
            _unfiltered = [];
            _list.Entries = [];
            _status.Text = e.Message;
        }
    }

    private void OnShellChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);

    private void OpenShellItem(ShellItem item)
    {
        if (item.ChildFolder is { } child) { Navigate(child); return; }
        if (item.FileSystemPath is { } path)
        {
            if (Directory.Exists(path)) Navigate(path);
            else if (File.Exists(path)) _openFile(path);
            return;
        }
        if (item.DefaultAction is { } action && _openShellFolder is not null)
        {
            _ = RunDefaultActionAsync(_openShellFolder, action, item);
            return;
        }
        _status.Text = $"\"{item.Name}\" cannot be opened.";
    }

    // The actions the folder offers for the selected item, or for the folder itself when nothing is selected.
    public IReadOnlyList<ShellAction> ShellActions
    {
        get
        {
            if (_openShellFolder is null) return [];
            var selected = _list.SelectedEntries;
            return _openShellFolder.GetActions(selected.Count == 1 ? selected[0].Tag as ShellItem : null);
        }
    }

    private async Task RunDefaultActionAsync(IShellFolder folder, string action, ShellItem item)
    {
        try { await folder.InvokeAsync(action, item, CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _status.Text = e.Message; }
    }

    public async Task InvokeShellActionAsync(string actionId)
    {
        if (_openShellFolder is null) return;
        var selected = _list.SelectedEntries;
        var item = selected.Count == 1 ? selected[0].Tag as ShellItem : null;
        if (!_openShellFolder.GetActions(item).Any(a => a.Id == actionId)) return;   // the folder does not offer it for this selection
        try { await _openShellFolder.InvokeAsync(actionId, item, CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _status.Text = e.Message; }
        Reload();
    }

    private void FillContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var action in ShellActions)
        {
            var entry = new MenuItem { Header = action.Label };
            var id = action.Id;
            entry.Click += async (_, _) => await InvokeShellActionAsync(id);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No actions", IsEnabled = false });
    }

    public void Navigate(string path) => Go(path, record: true);

    public void GoBack() => Go(_history.Back(), record: false);
    public void GoForward() => Go(_history.Forward(), record: false);
    public void GoUp() => Go(_history.Up(), record: false);

    // A folder opens here; a file is handed to the shell. Several selected files are all opened.
    public void Open(IReadOnlyList<FileEntry> items)
    {
        if (items.Count == 1 && items[0].Tag is ShellItem shellItem) { OpenShellItem(shellItem); return; }
        if (items.Count == 1 && items[0].IsDirectory)
        {
            Navigate(items[0].FullPath);
            return;
        }
        foreach (var item in items.Where(i => !i.IsDirectory))
            _openFile(item.FullPath);
    }

    public void Reload()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_openShellFolder is not null) { _openShellFolder.Changed -= OnShellChanged; _openShellFolder = null; }
        if (AddressPath.IsShellPath(_history.Current)) { ReloadShell(); return; }
        _list.PreserveOrder = false;
        Title = Path.GetFileName(_history.Current.TrimEnd('/')) is { Length: > 0 } name ? name : "/";
        _address.Path = _history.Current;
        _places.ShowCurrent(_history.Current);
        _back.IsEnabled = _history.CanGoBack;
        _forward.IsEnabled = _history.CanGoForward;
        _up.IsEnabled = _history.CanGoUp;

        try
        {
            _unfiltered = FolderLister.List(_history.Current, ShowHidden);
            ApplyFilter();
            _search.Watermark = "Search " + Title;
            _watcher = new FolderWatcher(_history.Current, () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unfiltered = [];
            _list.Entries = [];
            _status.Text = e is UnauthorizedAccessException ? "You do not have permission to open this folder." : "This folder cannot be opened: " + e.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }

    private void Go(string path, bool record)
    {
        if (record)
            _history.Navigate(path);
        Reload();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_address.IsEditing) return;   // typing a path: the keys belong to the text box
        var press = ShortcutInput.From(e);
        if (_shortcuts.ActionFor(press) is not { } action) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.Back: GoBack(); break;
            case ShortcutAction.Forward: GoForward(); break;
            case ShortcutAction.Up: GoUp(); break;
            case ShortcutAction.Refresh: Reload(); break;
            case ShortcutAction.Copy: _ = CopyAsync(); break;
            case ShortcutAction.Cut: _ = CutAsync(); break;
            case ShortcutAction.Paste: _ = PasteAsync(); break;
            case ShortcutAction.SelectAll: _list.SelectAll(); break;
            case ShortcutAction.NewFolder: NewFolder(); break;
            case ShortcutAction.DeletePermanently: _ = DeletePermanentlyAsync(); break;
            case ShortcutAction.Delete: DeleteSelected(); break;
            case ShortcutAction.Rename: _ = BeginRenameAsync(); break;
            case ShortcutAction.FocusAddress: _address.BeginEdit(); break;
            case ShortcutAction.Properties: ShowProperties(); break;
            case ShortcutAction.Open:
                if (press.Key != "Enter") Open(_list.SelectedEntries);   // plain Enter is the list's own
                else e.Handled = false;
                break;
        }
    }
}
