using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vde.Components;
using Vde.Shell;
using Vde.Shell.Controls;

namespace Vde.Shell.ComDlg;

// Open, Save and Choose Folder in one window: places and address bar to get around, the folder's files (only
// those that match the chosen filter), a file name box and OK/Cancel. What OK means is FileDialogRules' job.
public sealed class FileDialogWindow : Window
{
    private readonly FileDialogMode _mode;
    private readonly FileDialogOptions _options;
    private readonly NavigationHistory _history;
    private readonly AddressBar _address = new();
    private readonly PlacesSidebar _places = new();
    private readonly FileListView _list = new();
    private readonly TextBox _name = new() { MinWidth = 300 };
    private readonly ComboBox _filter = new() { MinWidth = 200 };
    private readonly TextBlock _message = new() { Foreground = Avalonia.Media.Brushes.IndianRed, IsVisible = false };
    private readonly IReadOnlyList<FileFilter> _filters;
    private readonly ShellVfs? _vfs;
    private readonly TextBox _search = new() { PlaceholderText = "Search", MinWidth = 190 };
    private readonly Button _back = Tool("\u2190", "Back"), _forward = Tool("\u2192", "Forward"), _up = Tool("\u2191", "Up one level"), _refresh = Tool("\u21BB", "Refresh");

    // askOverwrite: asked when a saved file already exists. Defaults to a message box over this window.
    public FileDialogWindow(FileDialogMode mode, FileDialogOptions options, KnownFolders folders, FileTypes? types = null, Func<string, Task<bool>>? askOverwrite = null, ShellVfs? vfs = null)
    {
        _vfs = vfs;
        _mode = mode;
        _options = options;
        AskOverwrite = askOverwrite ?? (async text =>
        {
            var box = new MessageBoxWindow(text, "Confirm", MessageButtons.YesNo, MessageIcon.Warning, DialogResult.No);
            await box.ShowDialog(this);
            return box.Result == DialogResult.Yes;
        });
        Title = options.Title ?? mode switch { FileDialogMode.Open => "Open", FileDialogMode.Save => "Save as", _ => "Choose a folder" };
        Width = 860; Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var start = options.InitialFolder is { } f && Directory.Exists(f) ? Path.GetFullPath(f) : folders.Get(KnownFolder.Home);
        _history = new NavigationHistory(start);
        _list.Types = types;
        _address.Home = folders.Get(KnownFolder.Home);
        // Folders, and with a shell folder catalog This PC, Network and the rest of what the sidebar holds (not Trash).
        _places.Places = Places.Default(folders, vfs?.Catalog).Where(p => p.Kind is PlaceKind.Folder or PlaceKind.ShellFolder).ToList();

        _filters = options.Filters.Count > 0 ? options.Filters : [FileFilter.All];
        _filter.ItemsSource = _filters.Select(x => x.Name).ToList();
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Reload();

        _address.PathRequested += (_, path) => { if (Directory.Exists(path) || _vfs?.Exists(path) == true) Go(path); };
        _places.PlaceInvoked += (_, place) => { if (place.Path is not null) Go(place.Path); };
        _list.SelectionChanged += (_, _) => FillNameFromSelection();
        _list.ItemsInvoked += async (_, items) =>
        {
            if (items.Count == 1 && items[0].Tag is ShellItem item) await OpenShellItemAsync(item);
            else if (items.Count == 1 && items[0].IsDirectory) Go(items[0].FullPath);
            else await AcceptAsync();
        };
        _name.Text = options.FileName ?? "";
        _back.Click += (_, _) => { _history.Back(); Reload(); };
        _forward.Click += (_, _) => { _history.Forward(); Reload(); };
        _up.Click += (_, _) => { _history.Up(); Reload(); };
        _refresh.Click += (_, _) => Reload();
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Reload(); };

        var ok = DialogHost.Button(options.OkText ?? (mode == FileDialogMode.Save ? "Save" : mode == FileDialogMode.Folder ? "Choose" : "Open"), () => _ = AcceptAsync(), isDefault: true);
        var cancel = DialogHost.Button("Cancel", () => { Paths = null; Close(); }, isCancel: true);
        OkButton = ok;

        // File name and type on one row, the buttons under them on the right.
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(12, 8), RowSpacing = 8, ColumnSpacing = 8 };
        bottom.Children.Add(Cell(new TextBlock { Text = mode == FileDialogMode.Folder ? "Folder:" : "File name:", VerticalAlignment = VerticalAlignment.Center }, 0, 0));
        bottom.Children.Add(Cell(_name, 1, 0));
        if (mode != FileDialogMode.Folder)
            bottom.Children.Add(Cell(_filter, 2, 0));
        var buttons = DialogHost.ButtonRow(ok, cancel);
        Grid.SetColumn(buttons, 1); Grid.SetColumnSpan(buttons, 2); Grid.SetRow(buttons, 1);
        bottom.Children.Add(buttons);
        _name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var footer = new StackPanel { Children = { _message, bottom } };

        var newFolder = new Button { Content = "New folder" };
        newFolder.Click += async (_, _) => await NewFolderAsync();
        var organize = new Button { Content = "Organize \u25BE", Flyout = new MenuFlyout { Items = { MenuItem("New folder", () => _ = NewFolderAsync()), MenuItem("Refresh", Reload) } } };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 2) };
        toolbar.Children.Add(organize);
        toolbar.Children.Add(newFolder);

        // Back/forward/up, the address bar, refresh and a search box that narrows the folder's list.
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        nav.Children.Add(_back); nav.Children.Add(_forward); nav.Children.Add(_up);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(8, 6), ColumnSpacing = 6 };
        top.Children.Add(Cell(nav, 0, 0));
        top.Children.Add(Cell(_address, 1, 0));
        top.Children.Add(Cell(_refresh, 2, 0));
        top.Children.Add(Cell(_search, 3, 0));
        var header = new StackPanel { Children = { top, toolbar } };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_places, Dock.Left);
        _places.Width = 170;
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_places);
        root.Children.Add(_list);
        Content = root;
        Closing += (_, _) => { };
        Reload();
    }

    public IReadOnlyList<string>? Paths { get; private set; }     // null: cancelled
    public Func<string, Task<bool>> AskOverwrite { get; }
    public string CurrentFolder => _history.Current;
    public IReadOnlyList<FileEntry> Entries => _list.Entries;
    public string Message => _message.IsVisible ? _message.Text ?? "" : "";
    public Button OkButton { get; }
    public string FileName { get => _name.Text ?? ""; set => _name.Text = value; }
    public int FilterIndex { get => _filter.SelectedIndex; set => _filter.SelectedIndex = value; }
    public FileListView List => _list;

    public void Go(string folder)
    {
        _history.Navigate(_vfs?.Normalize(folder) ?? folder);
        Reload();
    }

    // What the OK button does.
    public async Task AcceptAsync()
    {
        var folder = _history.Current;
        if (AddressPath.IsShellPath(folder))
        {
            Show("Open a drive or folder first.");
            return;
        }
        if (_mode == FileDialogMode.Folder)
        {
            var chosen = FileName.Length > 0 ? Path.GetFullPath(FileName, folder) : folder;
            if (!Directory.Exists(chosen)) { Show($"\"{FileName}\" is not a folder."); return; }
            Finish([chosen]);
            return;
        }

        var resolution = _mode == FileDialogMode.Save
            ? FileDialogRules.ResolveSave(folder, FileName, CurrentFilter, _options)
            : FileDialogRules.ResolveOpen(folder, FileName, _options);
        switch (resolution.Kind)
        {
            case Resolve.Navigate: _name.Text = ""; Go(resolution.Paths[0]); break;
            case Resolve.Invalid: Show(resolution.Message ?? "That cannot be used."); break;
            case Resolve.ConfirmOverwrite:
                if (await AskOverwrite(resolution.Message!)) Finish(resolution.Paths);
                break;
            default: Finish(resolution.Paths); break;
        }
    }

    private FileFilter CurrentFilter => _filters[Math.Max(0, _filter.SelectedIndex)];

    private void Finish(IReadOnlyList<string> paths)
    {
        Paths = paths;
        Close();
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }

    private void Reload()
    {
        _message.IsVisible = false;
        _address.Path = _history.Current;
        _back.IsEnabled = _history.CanGoBack;
        _forward.IsEnabled = _history.CanGoForward;
        _up.IsEnabled = _history.CanGoUp;
        _search.PlaceholderText = "Search " + (Path.GetFileName(_history.Current.TrimEnd('/')) is { Length: > 0 } leaf ? leaf : "/");
        _places.ShowCurrent(_history.Current);
        if (AddressPath.IsShellPath(_history.Current)) { ReloadShell(); return; }
        _list.PreserveOrder = false;
        try
        {
            var entries = FolderLister.List(_history.Current, _options.ShowHidden);
            // Folders always (to get around); files only for Open/Save and only the ones the filter allows.
            _list.Entries = entries.Where(e => (e.IsDirectory || (_mode != FileDialogMode.Folder && CurrentFilter.Matches(e.Name))) && Searched(e.Name)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _list.Entries = [];
            Show("This folder cannot be opened.");
        }
    }

    // A shell folder (This PC, Network) through the VFS: drives and places open on double-click.
    private void ReloadShell()
    {
        _list.PreserveOrder = true;
        try
        {
            var listing = (_vfs ?? throw new InvalidOperationException("This place is not available.")).List(_history.Current);
            _list.Entries = listing.Entries.Where(e => Searched(e.Name)).ToList();
        }
        catch (Exception e) when (e is ComponentLoadException or InvalidOperationException or IOException)
        {
            _list.Entries = [];
            Show(e.Message);
        }
    }

    private async Task OpenShellItemAsync(ShellItem item)
    {
        var open = ShellVfs.Open(item);
        if (open.Kind == VfsOpenKind.Enter) Go(open.Path!);
        else if (open.Kind == VfsOpenKind.OpenFile) { _name.Text = open.Path; await AcceptAsync(); }
    }

    private bool Searched(string name)
        => string.IsNullOrWhiteSpace(_search.Text) || name.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public string Search { get => _search.Text ?? ""; set => _search.Text = value; }

    // Makes a folder in the one being shown, named by the user, and goes on showing it.
    public async Task NewFolderAsync(string? name = null)
    {
        if (AddressPath.IsShellPath(_history.Current)) return;
        if (name is null)
        {
            var input = new InputWindow("Name:", "New folder", "New folder", validate: t => FileDialogRules.NameProblem(t));
            await DialogHost.RunAsync(input, this);
            name = input.Result;
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        try { Directory.CreateDirectory(Path.Combine(_history.Current, name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Show("The folder could not be created."); return; }
        Reload();
    }

    private static Button Tool(string glyph, string tip)
    {
        var b = new Button { Content = glyph, MinWidth = 30, Padding = new Thickness(4, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private void FillNameFromSelection()
    {
        var selected = _list.SelectedEntries;
        if (selected.Count == 0)
            return;
        if (_mode == FileDialogMode.Folder)
            _name.Text = selected.Count == 1 && selected[0].IsDirectory ? selected[0].Name : _name.Text;
        else if (selected.All(e => !e.IsDirectory))
            _name.Text = selected.Count == 1 ? selected[0].Name : string.Join(' ', selected.Select(e => "\"" + e.Name + "\""));
    }

    private static Control Cell(Control control, int column, int row)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }
}
