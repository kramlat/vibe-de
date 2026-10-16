using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.ComDlg;

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
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4) };
    private readonly Button _back = new() { Content = "◀" }, _forward = new() { Content = "▶" }, _up = new() { Content = "▲" }, _refresh = new() { Content = "⟳" };
    private readonly ToggleButton _hidden = new() { Content = "Hidden" };
    private FolderWatcher? _watcher;

    private readonly FileActions _actions;
    private readonly IFileClipboard _clipboard;

    public FileManagerWindow(string startFolder, KnownFolders folders, Action<string> openFile, FileTypes? types = null,
        FileActions? actions = null, IFileClipboard? clipboard = null)
    {
        _openFile = openFile;
        _actions = actions ?? new FileActions(new Trash());
        _clipboard = clipboard ?? new AvaloniaFileClipboard(() => GetTopLevel(this));
        Home = folders.Get(KnownFolder.Home);
        _history = new NavigationHistory(startFolder);
        Width = 960; Height = 600;

        _list.Types = types;
        _address.Home = Home;
        _places.Places = Places.Default(folders);

        _back.Click += (_, _) => Go(_history.Back(), record: false);
        _forward.Click += (_, _) => Go(_history.Forward(), record: false);
        _up.Click += (_, _) => Go(_history.Up(), record: false);
        _refresh.Click += (_, _) => Reload();
        _hidden.IsCheckedChanged += (_, _) => Reload();
        _address.PathRequested += (_, path) => Navigate(path);
        _places.PlaceInvoked += (_, place) =>
        {
            if (place.Kind == PlaceKind.Folder && place.Path is not null)
                Navigate(place.Path);
            else
                _status.Text = place.Name + " is not available in this window yet.";
        };
        _list.ItemsInvoked += (_, items) => Open(items);
        _list.SortChanged += (_, _) => Reload();
        KeyDown += OnKeyDown;
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        _list.InnerList.PointerPressed += (_, e) => _dragStart = e.GetPosition(_list);
        _list.InnerList.PointerMoved += OnPointerMoved;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6), Children = { _back, _forward, _up, _refresh, _hidden } };
        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Left);
        top.Children.Add(toolbar);
        top.Children.Add(_address);

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

        Reload();
    }

    public string Home { get; }
    public string CurrentFolder => _history.Current;
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    public IReadOnlyList<FileEntry> Entries => _list.Entries;
    public string StatusText => _status.Text ?? "";
    public bool ShowHidden { get => _hidden.IsChecked == true; set => _hidden.IsChecked = value; }

    // ---- file actions: the same ones the desktop has ----

    private Point? _dragStart;
    public string? Notice => StatusText;

    public void SelectEntries(params string[] names) => _list.Select(names);
    public IReadOnlyList<string> SelectedPaths => _list.SelectedEntries.Select(e => e.FullPath).ToList();

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
        try { _actions.Delete(SelectedPaths, permanent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        Reload();
    }

    public string? NewFolder()
    {
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
        foreach (var format in new[] { FileTransfer.UriList, FileTransfer.GnomeCopiedFiles })
            if (e.Data.Get(format) is { } value)
                formats[format] = value switch { byte[] b => b, string s => System.Text.Encoding.UTF8.GetBytes(s), _ => [] };
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
        var data = new DataObject();
        foreach (var (format, bytes) in new FileTransfer(TransferKind.Copy, SelectedPaths).ToFormats())
            data.Set(format, bytes);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    public void Navigate(string path) => Go(path, record: true);

    public void GoBack() => Go(_history.Back(), record: false);
    public void GoForward() => Go(_history.Forward(), record: false);
    public void GoUp() => Go(_history.Up(), record: false);

    // A folder opens here; a file is handed to the shell. Several selected files are all opened.
    public void Open(IReadOnlyList<FileEntry> items)
    {
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
        Title = Path.GetFileName(_history.Current.TrimEnd('/')) is { Length: > 0 } name ? name : "/";
        _address.Path = _history.Current;
        _places.ShowCurrent(_history.Current);
        _back.IsEnabled = _history.CanGoBack;
        _forward.IsEnabled = _history.CanGoForward;
        _up.IsEnabled = _history.CanGoUp;

        try
        {
            _list.Entries = FolderLister.List(_history.Current, ShowHidden);
            _status.Text = _list.Entries.Count == 1 ? "1 item" : $"{_list.Entries.Count} items";
            _watcher = new FolderWatcher(_history.Current, () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left) { GoBack(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right) { GoForward(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up) { GoUp(); e.Handled = true; }
        else if (e.Key == Key.F5) { Reload(); e.Handled = true; }
        else if (_address.IsEditing) return;   // typing a path: the keys below belong to the text box
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C) { _ = CopyAsync(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.X) { _ = CutAsync(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N) { NewFolder(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Delete) { _ = DeletePermanentlyAsync(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.F2) { _ = BeginRenameAsync(); e.Handled = true; }
        else if (e.Key == Key.Back && !_address.IsEditing) { GoUp(); e.Handled = true; }
    }
}
