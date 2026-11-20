using Vde.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.ComDlg;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// The desktop: the wallpaper, and the contents of ~/Desktop as icons on a grid. Icons can be dragged to a new
// cell (remembered in the registry), dropped onto a folder to move files into it, opened by double click, and
// cut, copied, renamed and deleted like in a file manager window. It watches the folder, so a file that appears
// on the desktop shows up at once.
public sealed class DesktopWindow : Window
{
    // The icon grid, sized the way Plasma's desktop does it (DesktopMetrics): from the icon size in the settings.
    private DesktopMetrics _metrics = new();
    private double ViewportWidth => Bounds.Width > 0 ? Bounds.Width : Width;
    private double ViewportHeight => Bounds.Height > 0 ? Bounds.Height : Height;
    public double CellWidth => _metrics.CellWidth(ViewportWidth);
    public double CellHeight => _metrics.CellHeight(ViewportHeight);
    public int IconSize => _metrics.Icon;

    private readonly string _folder;
    private readonly IHive _hive;
    private readonly ShellFolderCatalog? _shellFolders;   // the shell folders that want an icon on the desktop (This Computer)
    private readonly Action<string>? _openShellFolder;
    private IReadOnlyList<(string Key, string Name, IReadOnlyList<string> Icons, string Path)> _virtual = [];
    private readonly IHive? _defaults;   // the machine-wide desktop settings (the default wallpaper)
    private readonly Action<string> _open;
    private readonly Action? _openSettings;
    private readonly FileActions _actions;
    private readonly IFileClipboard _clipboard;
    private readonly FileTypes? _types;
    private readonly ShortcutStyleService _shortcuts;
    private readonly Func<IReadOnlyList<string>, PropertySheetContext>? _properties;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, Border> _icons = new(StringComparer.Ordinal);
    private WallpaperMode _mode = WallpaperMode.Fill;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<FileEntry> _entries = [];
    private IReadOnlyDictionary<string, Cell> _layout = new Dictionary<string, Cell>();
    private FolderWatcher? _watcher;

    public DesktopWindow(string desktopFolder, IHive hive, Action<string> openFile, FileTypes? types = null,
        FileActions? actions = null, IFileClipboard? clipboard = null, Action? openSettings = null, ShortcutStyleService? shortcuts = null,
        Func<IReadOnlyList<string>, PropertySheetContext>? properties = null, IHive? systemDefaults = null,
        ShellFolderCatalog? shellFolders = null, Action<string>? openShellFolder = null)
    {
        _shellFolders = shellFolders;
        _openShellFolder = openShellFolder;
        _properties = properties;
        _defaults = systemDefaults;
        _shortcuts = shortcuts ?? new ShortcutStyleService(new Hive("shortcuts"));
        _shortcuts.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _canvas.ContextMenu = BuildMenu());
        _folder = desktopFolder;
        _hive = hive;
        // The Desktop settings page changes these values: the wallpaper follows at once (icon positions are a subkey: not these).
        _hive.Changed += (_, c) => { if (c.KeyPath.Equals(DesktopSettingsStore.KeyPath, StringComparison.OrdinalIgnoreCase)) Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild); };
        _open = openFile;
        _types = types;
        _openSettings = openSettings;
        _actions = actions ?? new FileActions(new Trash());
        _clipboard = clipboard ?? new AvaloniaFileClipboard(() => GetTopLevel(this));

        Title = "Desktop";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        X11WindowHints.SetType(this, X11WindowHints.Kind.Desktop);
        WindowState = WindowState.Maximized;
        Width = 1280; Height = 720;
        Content = _canvas;
        Focusable = true;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => LightFolder(null));
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
        PointerPressed += (_, e) =>
        {
            if (e.Source is not null && !_icons.Values.Any(i => e.Source is Visual v && (v == i || i.IsVisualAncestorOf(v))))
                ClearSelection();   // a click on the bare desktop deselects
            Focus();
        };
        ActualThemeVariantChanged += (_, _) => Rebuild();
        SizeChanged += (_, _) => Rebuild();

        _canvas.ContextMenu = BuildMenu();
        Reload();
    }

    public IReadOnlyList<FileEntry> Entries => _entries;
    public IReadOnlyCollection<string> SelectedNames => _selected;
    public int Columns => Math.Max(1, (int)(ViewportWidth / CellWidth));
    public int Rows => Math.Max(1, (int)(ViewportHeight / CellHeight));
    public IReadOnlyList<string> MenuLabels => _canvas.ContextMenu!.Items.OfType<MenuItem>().Select(m => ((Grid)m.Header!).Children.OfType<TextBlock>().First().Text ?? "").ToList();
    public IReadOnlyList<string> MenuShortcuts => _canvas.ContextMenu!.Items.OfType<MenuItem>().Select(m => ((Grid)m.Header!).Children.OfType<TextBlock>().Last().Text ?? "").ToList();
    public ShortcutStyle ShortcutStyle => _shortcuts.Style;
    public Cell CellOf(string name) => _layout[name];
    public bool IconsVisible => _canvas.Children.Any(c => c is Border);

    // ---- contents ----

    public void Reload()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _entries = FolderLister.Sort(FolderLister.List(_folder), SortKey.Name)
                .Where(e => e.IsDirectory || FileLook.Launcher(e.FullPath, []) is not { Hidden: true }).ToList();   // Hidden=true launchers are deleted ones: no desktop shows them
            _watcher = new FolderWatcher(_folder, () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }
        _virtual = LoadVirtualIcons();
        var names = _virtual.Select(v => v.Key).Concat(_entries.Select(e => e.Name)).ToList();
        DesktopSettingsStore.ForgetMissing(_hive, names);
        _selected.IntersectWith(names);
        Rebuild();
    }

    private void Rebuild()
    {
        var settings = DesktopSettingsStore.Load(_hive, _defaults);
        Background = WallpaperBrush.Create(settings);
        _mode = settings.Mode;
        _metrics = new DesktopMetrics(settings.IconSize);
        _canvas.Children.Clear();
        _icons.Clear();
        // the shell folders first (This Computer, as the first icon), then the files of the desktop folder
        _layout = DesktopLayout.Arrange(_virtual.Select(v => v.Key).Concat(_entries.Select(e => e.Name)), DesktopSettingsStore.LoadPositions(_hive), Columns, Rows);
        if (!settings.ShowIcons)
            return;

        foreach (var v in _virtual)
        {
            var icon = BuildIcon(v.Key, v.Name, v.Icons);
            _icons[v.Key] = icon;
            Place(icon, _layout[v.Key]);
            _canvas.Children.Add(icon);
        }
        foreach (var entry in _entries)
        {
            var icon = MakeIcon(entry);
            _icons[entry.Name] = icon;
            Place(icon, _layout[entry.Name]);
            _canvas.Children.Add(icon);
        }
    }

    // Whole pixels only: an icon on a fractional position is drawn blurred (Plasma floors for the same reason).
    private void Place(Control icon, Cell cell)
    {
        Canvas.SetLeft(icon, Math.Floor(cell.Column * CellWidth + _metrics.SmallSpacing));
        Canvas.SetTop(icon, Math.Floor(cell.Row * CellHeight + _metrics.SmallSpacing));
    }

    // The shell folders registered for the Desktop place. Their icons are keyed "::{clsid}" (the parsing form of the CLSID),
    // which is what their cell and selection are remembered under; no file on the desktop has that name.
    private IReadOnlyList<(string Key, string Name, IReadOnlyList<string> Icons, string Path)> LoadVirtualIcons()
    {
        if (_shellFolders is null) return [];
        var list = new List<(string, string, IReadOnlyList<string>, string)>();
        foreach (var info in _shellFolders.Namespace("Desktop"))
        {
            IReadOnlyList<string> icons = ["folder"];
            try { icons = _shellFolders.Open(info).IconNames; }
            catch (Exception e) when (e is ComponentLoadException or InvalidOperationException) { }   // an untrusted component: still shown, with a plain icon
            list.Add(("::" + info.Clsid, info.Name, icons, info.Path));
        }
        return list;
    }

    // The picture and name come the way GNOME and KDE choose them (FileLook): a launcher's own Icon= and Name=, a cached
    // thumbnail, then the file type's icon. The type's icons stay the fallback after a thumbnail.
    private Border MakeIcon(FileEntry entry)
    {
        if (!entry.IsDirectory && FileLook.Launcher(entry.FullPath, Locale.FromEnvironment()) is { } launcher)
            return BuildIcon(entry.Name, launcher.Name ?? Path.GetFileNameWithoutExtension(entry.Name), launcher.IconNames!, hidden: entry.IsHidden, link: entry.IsSymlink);
        if (entry.IsDirectory)
            return BuildIcon(entry.Name, entry.Name, FolderIcon.NamesFor(entry.FullPath), hidden: entry.IsHidden, link: entry.IsSymlink);
        var names = (_types?.Describe(entry.FullPath).IconNames ?? ["text-x-generic"]).ToList();
        if (FileLook.Thumbnail(entry.FullPath) is { } thumb)
            names.Insert(0, thumb);
        return BuildIcon(entry.Name, entry.Name, names, hidden: entry.IsHidden, link: entry.IsSymlink);
    }

    // One icon, laid out as in Plasma's FolderItemDelegate: the picture centred at the top, the name under it in
    // white with a dark edge (it is drawn straight on the wallpaper), and a highlight frame that follows the
    // contents and is inset by the small spacing from the cell.
    private Border BuildIcon(string key, string text, IReadOnlyList<string> iconNames, bool hidden = false, bool link = false)
    {
        double ss = _metrics.SmallSpacing;
        var label = new OutlinedLabel
        {
            Text = text, MaxLines = _metrics.TextLines, FontSize = 13, Italic = link,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, ss, 0, 0),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, OffsetX = 0, OffsetY = 1, Opacity = 0.7 },
        };
        var border = new Border
        {
            Width = _metrics.FrameWidth(CellWidth), MaxHeight = CellHeight - 2 * ss,
            CornerRadius = new CornerRadius(5), Padding = new Thickness(ss),
            Tag = key, Opacity = hidden ? 0.6 : 1,
            Background = _selected.Contains(key) ? ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray) : Brushes.Transparent,
            Child = new StackPanel
            {
                // the artwork floats on a soft shadow (Plasma: radius 5, one pixel down, 60% black)
                Children =
                {
                    new ShellIcon
                    {
                        IconNames = iconNames, IconSize = _metrics.Icon, HorizontalAlignment = HorizontalAlignment.Center,
                        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, OffsetX = 0, OffsetY = 1, Opacity = 0.6 },
                    },
                    label,
                },
            },
        };
        border.PointerPressed += (_, e) => OnIconPressed(key, border, e);
        border.PointerMoved += (_, e) => OnIconMoved(border, e);
        border.PointerReleased += (_, e) => OnIconReleased(border, e);
        border.DoubleTapped += (_, _) => OpenSelected();
        var itemMenu = new ContextMenu();   // an icon has its own menu, not the desktop's: it acts on what is selected
        itemMenu.Opening += (_, _) =>
        {
            if (!_selected.Contains(key)) SelectIcon(key);
            FileContextMenus.Fill(itemMenu, ItemMenu(), _shortcuts.Style);
        };
        border.ContextMenu = itemMenu;
        return border;
    }

    // ---- selection ----

    public void SelectIcon(string name, bool add = false)
    {
        if (!add) _selected.Clear();
        if (!add || !_selected.Remove(name)) _selected.Add(name);   // ctrl+click toggles
        Refresh();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (name, icon) in _icons)
            icon.Background = _selected.Contains(name) ? ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray) : Brushes.Transparent;
    }

    private IReadOnlyList<string> SelectedPaths => _entries.Where(e => _selected.Contains(e.Name)).Select(e => e.FullPath).ToList();

    public void OpenSelected()
    {
        foreach (var v in _virtual.Where(v => _selected.Contains(v.Key)))
            _openShellFolder?.Invoke(v.Path);
        foreach (var path in SelectedPaths)
            _open(path);   // folders too: the shell opens them in a file manager window
    }

    // ---- moving icons and dragging files ----

    // A drag starts from an icon with a real drag and drop, so the files can be dropped on the desktop itself (the icon
    // takes that place), on a folder icon here, or on another program's window (a file manager copies or moves them).
    private (string name, Point grab)? _drag;
    private PointerPressedEventArgs? _dragTrigger;
    private string? _dragName;   // the icon being dragged from here, while the drag lasts

    private void OnIconPressed(string name, Border icon, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(icon).Properties.IsLeftButtonPressed) return;
        if (!_selected.Contains(name) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            SelectIcon(name, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _drag = (name, e.GetPosition(_canvas));
        _dragTrigger = e;
        e.Handled = true;
    }

    private async void OnIconMoved(Border icon, PointerEventArgs e)
    {
        if (_drag is not { } d || (string)icon.Tag! != d.name || !e.GetCurrentPoint(icon).Properties.IsLeftButtonPressed) return;
        var now = e.GetPosition(_canvas);
        if (Math.Abs(now.X - d.grab.X) + Math.Abs(now.Y - d.grab.Y) < 6) return;   // a click, not a drag
        _drag = null;
        if (_dragTrigger is not { } trigger || SelectedPaths.Count == 0) return;
        _dragName = d.name;
        try { await DragDrop.DoDragDropAsync(trigger, DropData.Of(SelectedPaths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        finally { _dragName = null; LightFolder(null); }
    }

    private void OnIconReleased(Border icon, PointerReleasedEventArgs e) => _drag = null;

    // The folder icon under a point of the desktop, if any (the one being dragged does not count).
    private FileEntry? FolderAt(Point point)
    {
        var cell = DesktopLayout.CellAt(point.X, point.Y, CellWidth, CellHeight, Columns, Rows);
        return _entries.FirstOrDefault(en => en.IsDirectory && en.Name != _dragName && !(_dragName is not null && _selected.Contains(en.Name)) && _layout.TryGetValue(en.Name, out var c) && c == cell);
    }

    private string? _lit;

    // Lights the folder icon a drop would go into (and puts the last one back).
    private void LightFolder(string? name)
    {
        if (_lit == name) return;
        if (_lit is not null && _icons.TryGetValue(_lit, out var old)) old.Background = _selected.Contains(_lit) ? ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray) : Brushes.Transparent;
        _lit = name;
        if (name is not null && _icons.TryGetValue(name, out var icon)) icon.Background = ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray);
    }

    // A drag from here onto bare desktop moves the icon; anything else is a copy, move or link into a folder.
    private DropEffect DecideDrop(DragEventArgs e, out string? targetFolder, out bool reposition)
    {
        var sources = DropData.Paths(e);
        var folder = FolderAt(e.GetPosition(_canvas));
        targetFolder = folder?.FullPath;
        reposition = _dragName is not null && folder is null;
        if (reposition) return DropEffect.Move;
        return sources.Count == 0 ? DropEffect.None
            : DropRules.Decide(sources, targetFolder ?? _folder, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var effect = DecideDrop(e, out var target, out _);
        LightFolder(target is not null && effect != DropEffect.None ? _entries.First(en => en.FullPath == target).Name : null);
        e.DragEffects = DropData.Effects(effect);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var effect = DecideDrop(e, out var target, out var reposition);
        LightFolder(null);
        e.Handled = true;
        if (reposition)
        {
            var at = e.GetPosition(_canvas);
            DropIcon(_dragName!, DesktopLayout.CellAt(at.X, at.Y, CellWidth, CellHeight, Columns, Rows));
        }
        else if (effect != DropEffect.None)
            HandleDrop(DropData.Paths(e), target, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    // The icon was let go on this cell: onto a folder it moves the selection into it, otherwise it takes the cell.
    public void DropIcon(string name, Cell cell)
    {
        var onFolder = _entries.FirstOrDefault(e => e.IsDirectory && e.Name != name && !_selected.Contains(e.Name) && _layout.TryGetValue(e.Name, out var c) && c == cell);
        if (onFolder is not null)
        {
            HandleDrop(SelectedPaths, onFolder.FullPath, false, false);
            return;
        }
        // A cell another icon sits on is taken: this one goes back where it was rather than stacking.
        if (_layout.Any(kv => kv.Key != name && kv.Value == cell)) { Rebuild(); return; }
        DesktopSettingsStore.SetPosition(_hive, name, cell);
        Rebuild();
    }

    // Properties of the selected icons, or of the desktop folder when nothing is selected.
    public PropertySheetWindow? ShowProperties()
    {
        var paths = SelectedPaths.Count > 0 ? SelectedPaths : [_folder];
        var context = _properties?.Invoke(paths) ?? new PropertySheetContext { Target = new PropertyTarget(paths, ""), Actions = _actions, BuiltInProviders = [(PropertyPages.Clsid, new PropertyPages())] };
        try
        {
            var sheet = new PropertySheetWindow(context);
            sheet.Show();
            return sheet;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- actions (the same ones a file manager window has) ----

    public async Task CopyAsync() { if (SelectedPaths.Count > 0) await _clipboard.SetAsync(new FileTransfer(TransferKind.Copy, SelectedPaths)); }
    public async Task CutAsync() { if (SelectedPaths.Count > 0) await _clipboard.SetAsync(new FileTransfer(TransferKind.Cut, SelectedPaths)); }

    public async Task PasteAsync()
    {
        if (await _clipboard.GetAsync() is not { } transfer) return;
        try
        {
            _actions.Paste(transfer, _folder, new FileOperationOptions { OnConflict = (_, _) => ConflictAction.KeepBoth });
            if (transfer.Kind == TransferKind.Cut) await _clipboard.SetAsync(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Reload();
    }

    public void DeleteSelected(bool permanent = false)
    {
        try { _actions.Delete(SelectedPaths, permanent); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Reload();
    }

    public string? NewFolder()
    {
        try
        {
            var path = _actions.NewFolder(_folder);
            Reload();
            SelectIcon(Path.GetFileName(path));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Rename(string newName)
    {
        var selected = SelectedPaths;
        if (selected.Count != 1 || FileDialogRules.NameProblem(newName) is not null) return false;
        try
        {
            var oldName = Path.GetFileName(selected[0]);
            _actions.Rename(selected[0], newName);
            if (DesktopSettingsStore.LoadPositions(_hive).TryGetValue(oldName, out var cell))   // it keeps its place
                DesktopSettingsStore.SetPosition(_hive, newName, cell);
            _selected.Clear();
            Reload();
            SelectIcon(newName);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<string> HandleDrop(IReadOnlyList<string> sources, string? targetFolder, bool ctrl, bool shift, bool alt = false)
    {
        var target = targetFolder ?? _folder;
        var effect = DropRules.Decide(sources, target, ctrl, shift, alt: alt);
        if (effect == DropEffect.None) return [];
        try { return _actions.Drop(sources, target, effect, new FileOperationOptions { OnConflict = (_, _) => ConflictAction.KeepBoth }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        finally { Reload(); }
    }

    private async Task RenameWithDialogAsync()
    {
        if (SelectedPaths.Count != 1) return;
        var current = Path.GetFileName(SelectedPaths[0]);
        var name = await CommonDialogs.InputAsync(this, "New name:", "Rename", current, FileDialogRules.NameProblem);
        if (name is not null && name != current) Rename(name);
    }

    // The menu of the bare desktop, as Explorer's: view, refresh, paste, new, and the desktop's own settings.
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) => FileContextMenus.Fill(menu, BackgroundMenu(), _shortcuts.Style);
        FileContextMenus.Fill(menu, BackgroundMenu(), _shortcuts.Style);
        return menu;
    }

    private static readonly (string Label, int Size)[] ViewSizes = [("Small icons", 32), ("Medium icons", 48), ("Large icons", 64), ("Extra large icons", 96)];

    public IReadOnlyList<MenuEntry?> BackgroundMenu()
    {
        var settings = DesktopSettingsStore.Load(_hive, _defaults);
        void Change(Func<DesktopSettings, DesktopSettings> edit) => DesktopSettingsStore.Save(_hive, edit(DesktopSettingsStore.Load(_hive, _defaults)));
        return
        [
            new("View", Sub:
            [
                .. ViewSizes.Select(v => (MenuEntry?)new MenuEntry(v.Label, null, () => Change(s => s with { IconSize = v.Size }), Checked: IconSizes.Snap(settings.IconSize) == IconSizes.Snap(v.Size))),
                null,
                new("Show desktop icons", null, () => Change(s => s with { ShowIcons = !s.ShowIcons }), Checked: settings.ShowIcons),
            ]),
            new("Refresh", ShortcutAction.Refresh, Reload),
            null,
            new("Paste", ShortcutAction.Paste, () => _ = PasteAsync()),
            null,
            new("New", Sub: [new("Folder", ShortcutAction.NewFolder, () => NewFolder()), new("Text document", null, () => NewTextDocument())]),
            null,
            new("Properties", ShortcutAction.Properties, () => ShowProperties()),
            null,
            new("Desktop settings", null, () => _openSettings?.Invoke()),
        ];
    }

    // What a right click on an icon offers: the same file actions a file manager window has.
    public IReadOnlyList<MenuEntry?> ItemMenu()
    {
        var onlyShellFolders = SelectedPaths.Count == 0 && _virtual.Any(v => _selected.Contains(v.Key));
        if (onlyShellFolders)
            return [new("Open", ShortcutAction.Open, OpenSelected)];
        return
        [
            new("Open", ShortcutAction.Open, OpenSelected),
            null,
            new("Cut", ShortcutAction.Cut, () => _ = CutAsync()),
            new("Copy", ShortcutAction.Copy, () => _ = CopyAsync()),
            new("Create shortcut", null, () => CreateShortcuts()),
            null,
            new("Delete", ShortcutAction.Delete, () => DeleteSelected()),
            new("Rename", ShortcutAction.Rename, () => _ = RenameWithDialogAsync(), Enabled: SelectedPaths.Count == 1),
            null,
            new("Properties", ShortcutAction.Properties, () => ShowProperties()),
        ];
    }

    public IReadOnlyList<string> CreateShortcuts()
    {
        try { return _actions.CreateLinks(SelectedPaths, _folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        finally { Reload(); }
    }

    public string? NewTextDocument()
    {
        try
        {
            var path = _actions.NewTextDocument(_folder);
            Reload();
            SelectIcon(Path.GetFileName(path));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shortcuts.ActionFor(ShortcutInput.From(e)) is not { } action) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.Open: OpenSelected(); break;
            case ShortcutAction.Copy: _ = CopyAsync(); break;
            case ShortcutAction.Cut: _ = CutAsync(); break;
            case ShortcutAction.Paste: _ = PasteAsync(); break;
            case ShortcutAction.SelectAll: foreach (var n in _entries) _selected.Add(n.Name); Refresh(); break;
            case ShortcutAction.NewFolder: NewFolder(); break;
            case ShortcutAction.Delete: DeleteSelected(); break;
            case ShortcutAction.DeletePermanently: DeleteSelected(permanent: true); break;
            case ShortcutAction.Rename: _ = RenameWithDialogAsync(); break;
            case ShortcutAction.Refresh: Reload(); break;
            case ShortcutAction.Properties: ShowProperties(); break;
            default: e.Handled = false; break;   // back, forward, up and the address bar mean nothing on the desktop
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
