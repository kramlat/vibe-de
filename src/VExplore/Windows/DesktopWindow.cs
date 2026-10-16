using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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
    public const double CellWidth = 96, CellHeight = 100, Margin_ = 12;

    private readonly string _folder;
    private readonly IHive _hive;
    private readonly Action<string> _open;
    private readonly Action? _openSettings;
    private readonly FileActions _actions;
    private readonly IFileClipboard _clipboard;
    private readonly FileTypes? _types;
    private readonly ShortcutStyleService _shortcuts;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, Border> _icons = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<FileEntry> _entries = [];
    private IReadOnlyDictionary<string, Cell> _layout = new Dictionary<string, Cell>();
    private FolderWatcher? _watcher;
    private (string name, Point grab, Point origin)? _drag;
    private bool _moved;

    public DesktopWindow(string desktopFolder, IHive hive, Action<string> openFile, FileTypes? types = null,
        FileActions? actions = null, IFileClipboard? clipboard = null, Action? openSettings = null, ShortcutStyleService? shortcuts = null)
    {
        _shortcuts = shortcuts ?? new ShortcutStyleService(new Hive("shortcuts"));
        _shortcuts.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _canvas.ContextMenu = BuildMenu());
        _folder = desktopFolder;
        _hive = hive;
        _open = openFile;
        _types = types;
        _openSettings = openSettings;
        _actions = actions ?? new FileActions(new Trash());
        _clipboard = clipboard ?? new AvaloniaFileClipboard(() => GetTopLevel(this));

        Title = "Desktop";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Width = 1280; Height = 720;
        Content = _canvas;
        Focusable = true;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Move | DragDropEffects.Copy; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var formats = new Dictionary<string, byte[]>();
            foreach (var f in new[] { FileTransfer.UriList, FileTransfer.GnomeCopiedFiles })
                if (e.Data.Get(f) is { } v) formats[f] = v switch { byte[] b => b, string s => System.Text.Encoding.UTF8.GetBytes(s), _ => [] };
            if (FileTransfer.FromFormats(formats) is { } t)
                HandleDrop(t.Paths, null, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        });
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
    public int Columns => Math.Max(1, (int)((Bounds.Width > 0 ? Bounds.Width : Width) / CellWidth));
    public int Rows => Math.Max(1, (int)((Bounds.Height > 0 ? Bounds.Height : Height) / CellHeight));
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
            _entries = FolderLister.Sort(FolderLister.List(_folder), SortKey.Name);
            _watcher = new FolderWatcher(_folder, () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }
        DesktopSettingsStore.ForgetMissing(_hive, _entries.Select(e => e.Name));
        _selected.IntersectWith(_entries.Select(e => e.Name));
        Rebuild();
    }

    private void Rebuild()
    {
        var settings = DesktopSettingsStore.Load(_hive);
        Background = Wallpaper(settings);
        _canvas.Children.Clear();
        _icons.Clear();
        _layout = DesktopLayout.Arrange(_entries.Select(e => e.Name), DesktopSettingsStore.LoadPositions(_hive), Columns, Rows);
        if (!settings.ShowIcons)
            return;

        foreach (var entry in _entries)
        {
            var icon = MakeIcon(entry);
            _icons[entry.Name] = icon;
            Place(icon, _layout[entry.Name]);
            _canvas.Children.Add(icon);
        }
    }

    private static void Place(Control icon, Cell cell)
    {
        Canvas.SetLeft(icon, Margin_ + cell.Column * CellWidth);
        Canvas.SetTop(icon, Margin_ + cell.Row * CellHeight);
    }

    private Border MakeIcon(FileEntry entry)
    {
        var info = entry.IsDirectory ? null : _types?.Describe(entry.FullPath);
        var label = new TextBlock
        {
            Text = entry.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, FontSize = 12,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var border = new Border
        {
            Width = CellWidth - 6, Height = CellHeight - 6, CornerRadius = new CornerRadius(4), Padding = new Thickness(2, 6),
            Tag = entry.Name,
            Background = _selected.Contains(entry.Name) ? ThemeTokens.Find(this, ThemeToken.SelectedFill, Brushes.Gray) : Brushes.Transparent,
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { new ShellIcon { IconNames = entry.IsDirectory ? ["folder"] : info?.IconNames ?? ["text-x-generic"], IconSize = 48, HorizontalAlignment = HorizontalAlignment.Center }, label },
            },
        };
        border.PointerPressed += (_, e) => OnIconPressed(entry.Name, border, e);
        border.PointerMoved += (_, e) => OnIconMoved(border, e);
        border.PointerReleased += (_, e) => OnIconReleased(border, e);
        border.DoubleTapped += (_, _) => OpenSelected();
        return border;
    }

    private IBrush Wallpaper(DesktopSettings settings)
    {
        var color = Color.TryParse(settings.BackgroundColor, out var c) ? c : Color.Parse("#1E1E2E");
        if (settings.Wallpaper is { } path && File.Exists(path))
            try
            {
                return new ImageBrush(new Bitmap(path))
                {
                    Stretch = settings.Mode switch { WallpaperMode.Fit => Stretch.Uniform, WallpaperMode.Stretch => Stretch.Fill, WallpaperMode.Center => Stretch.None, WallpaperMode.Tile => Stretch.None, _ => Stretch.UniformToFill },
                    TileMode = settings.Mode == WallpaperMode.Tile ? TileMode.Tile : TileMode.None,
                    AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
                };
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { }   // an unreadable image: just the colour
        return new SolidColorBrush(color);
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
        foreach (var path in SelectedPaths)
            _open(path);   // folders too: the shell opens them in a file manager window
    }

    // ---- moving icons ----

    private void OnIconPressed(string name, Border icon, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(icon).Properties.IsLeftButtonPressed) return;
        if (!_selected.Contains(name) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            SelectIcon(name, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _drag = (name, e.GetPosition(_canvas), new Point(Canvas.GetLeft(icon), Canvas.GetTop(icon)));
        _moved = false;
        e.Pointer.Capture(icon);
        e.Handled = true;
    }

    private void OnIconMoved(Border icon, PointerEventArgs e)
    {
        if (_drag is not { } d || (string)icon.Tag! != d.name) return;
        var now = e.GetPosition(_canvas);
        if (!_moved && Math.Abs(now.X - d.grab.X) + Math.Abs(now.Y - d.grab.Y) < 6) return;   // a click, not a drag
        _moved = true;
        Canvas.SetLeft(icon, d.origin.X + now.X - d.grab.X);
        Canvas.SetTop(icon, d.origin.Y + now.Y - d.grab.Y);
        icon.ZIndex = 10;
    }

    private void OnIconReleased(Border icon, PointerReleasedEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        e.Pointer.Capture(null);
        icon.ZIndex = 0;
        if (!_moved) return;

        var center = new Point(Canvas.GetLeft(icon) + CellWidth / 2, Canvas.GetTop(icon) + CellHeight / 2);
        var cell = DesktopLayout.CellAt(center.X - Margin_, center.Y - Margin_, CellWidth, CellHeight, Columns, Rows);
        DropIcon(d.name, cell);
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

    public IReadOnlyList<string> HandleDrop(IReadOnlyList<string> sources, string? targetFolder, bool ctrl, bool shift)
    {
        var target = targetFolder ?? _folder;
        var effect = DropRules.Decide(sources, target, ctrl, shift);
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

    private ContextMenu BuildMenu()
    {
        var s = _shortcuts.Style;
        MenuItem Item(string text, ShortcutAction? action, Action click)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 200 };
            header.Children.Add(new TextBlock { Text = text });
            var hint = new TextBlock { Text = action is { } a ? ShortcutMap.Label(a, s) : "", Opacity = 0.6, Margin = new Thickness(24, 0, 0, 0) };
            Grid.SetColumn(hint, 1);
            header.Children.Add(hint);
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
        }
        return new ContextMenu
        {
            Items =
            {
                Item("New folder", ShortcutAction.NewFolder, () => NewFolder()),
                Item("Paste", ShortcutAction.Paste, () => _ = PasteAsync()),
                Item("Refresh", ShortcutAction.Refresh, Reload),
                new Separator(),
                Item("Desktop settings", null, () => _openSettings?.Invoke()),
            },
        };
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
            default: e.Handled = false; break;   // back, forward, up and the address bar mean nothing on the desktop
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
