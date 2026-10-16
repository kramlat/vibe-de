using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Vde.Shell;
using Vde.Shell.Controls;

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

    public FileManagerWindow(string startFolder, KnownFolders folders, Action<string> openFile, FileTypes? types = null)
    {
        _openFile = openFile;
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
        else if (e.Key == Key.Back && !_address.IsEditing) { GoUp(); e.Handled = true; }
    }
}
