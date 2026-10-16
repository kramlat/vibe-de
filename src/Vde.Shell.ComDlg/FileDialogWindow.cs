using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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

    // askOverwrite: asked when a saved file already exists. Defaults to a message box over this window.
    public FileDialogWindow(FileDialogMode mode, FileDialogOptions options, KnownFolders folders, FileTypes? types = null, Func<string, Task<bool>>? askOverwrite = null)
    {
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
        _places.Places = Places.Default(folders).Where(p => p.Kind == PlaceKind.Folder).ToList();

        _filters = options.Filters.Count > 0 ? options.Filters : [FileFilter.All];
        _filter.ItemsSource = _filters.Select(x => x.Name).ToList();
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Reload();

        _address.PathRequested += (_, path) => { if (Directory.Exists(path)) Go(path); };
        _places.PlaceInvoked += (_, place) => { if (place.Path is not null) Go(place.Path); };
        _list.SelectionChanged += (_, _) => FillNameFromSelection();
        _list.ItemsInvoked += async (_, items) =>
        {
            if (items.Count == 1 && items[0].IsDirectory) Go(items[0].FullPath);
            else await AcceptAsync();
        };
        _name.Text = options.FileName ?? "";

        var ok = DialogHost.Button(mode == FileDialogMode.Save ? "Save" : mode == FileDialogMode.Folder ? "Choose" : "Open", () => _ = AcceptAsync(), isDefault: true);
        var cancel = DialogHost.Button("Cancel", () => { Paths = null; Close(); }, isCancel: true);
        OkButton = ok;

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(8), RowSpacing = 6, ColumnSpacing = 8 };
        bottom.Children.Add(Cell(new TextBlock { Text = mode == FileDialogMode.Folder ? "Folder:" : "File name:", VerticalAlignment = VerticalAlignment.Center }, 0, 0));
        bottom.Children.Add(Cell(_name, 1, 0));
        bottom.Children.Add(Cell(ok, 2, 0));
        if (mode != FileDialogMode.Folder)
            bottom.Children.Add(Cell(_filter, 1, 1));
        bottom.Children.Add(Cell(cancel, 2, 1));
        var footer = new StackPanel { Children = { _message, bottom } };

        var root = new DockPanel();
        DockPanel.SetDock(_address, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_places, Dock.Left);
        _places.Width = 170;
        root.Children.Add(_address);
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
        _history.Navigate(folder);
        Reload();
    }

    // What the OK button does.
    public async Task AcceptAsync()
    {
        var folder = _history.Current;
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
        _places.ShowCurrent(_history.Current);
        try
        {
            var entries = FolderLister.List(_history.Current, _options.ShowHidden);
            // Folders always (to get around); files only for Open/Save and only the ones the filter allows.
            _list.Entries = entries.Where(e => e.IsDirectory || (_mode != FileDialogMode.Folder && CurrentFilter.Matches(e.Name))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _list.Entries = [];
            Show("This folder cannot be opened.");
        }
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
