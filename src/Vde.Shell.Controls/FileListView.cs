using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// A folder's contents in one of the views a file manager offers: the details table (icon and name, date modified,
// type and size, with sortable headings), Windows XP's tiles (icon, name and two lines of detail, with Vista's
// fullness bar under a drive) and icon grids from small to extra large, where a folder can show a peek at what is
// inside it. Any view can be grouped under headings (name, type, size, date). It shows what it is given;
// navigation, sorting and opening are decisions for whoever hosts it.
public sealed class FileListView : UserControl
{
    private const double ModifiedWidth = 150, TypeWidth = 150, SizeWidth = 90;

    private sealed record GroupHeader(string Title, int Count);

    // Group headings sit in the list as items but are never selected or focused.
    private sealed class ItemsBox : ListBox
    {
        protected override Type StyleKeyOverride => typeof(ListBox);

        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            bool heading = item is GroupHeader;
            container.Focusable = !heading;
            container.IsHitTestVisible = !heading;
        }
    }

    private readonly ItemsBox _list = new() { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Grid _header = new() { ColumnDefinitions = Columns() };
    private readonly Control _divider;
    private IReadOnlyList<FileEntry> _raw = [];
    private IReadOnlyList<FileEntry> _entries = [];
    private ViewMode _mode = ViewMode.Details;
    private GroupBy _groupBy = GroupBy.None;
    private bool _folderPreviews = true, _fixingSelection;

    public FileListView()
    {
        var headings = new[] { ("Name", SortKey.Name), ("Date modified", SortKey.Modified), ("Type", SortKey.Type), ("Size", SortKey.Size) };
        for (int i = 0; i < headings.Length; i++)
        {
            var (text, key) = headings[i];
            var button = new TextBlock { Text = text, Margin = new Thickness(i == 0 ? 30 : 8, 4), FontWeight = FontWeight.SemiBold, Tag = key };
            button.PointerPressed += (_, _) => RequestSort(key);
            Grid.SetColumn(button, i);
            _header.Children.Add(button);
        }

        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            GroupHeader heading => Heading(heading),
            FileEntry entry => Item(entry),
            _ => null,
        });
        _list.DoubleTapped += (_, _) => InvokeSelected();
        _list.SelectionChanged += (_, _) =>
        {
            DropHeadingsFromSelection();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && EnterOpens) { InvokeSelected(); e.Handled = true; }
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
        // the line under the column headings, as in Windows 11's Explorer
        _divider = new Border
        {
            Height = 1,
            Background = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            Margin = new Thickness(6, 0, 6, 2),
        };
        DockPanel.SetDock(_divider, Dock.Top);
        layout.Children.Add(_divider);
        layout.Children.Add(_list);
        Content = layout;
        ApplyMode();
    }

    // Used to find each file's icon and description; without it files get generic icons.
    public FileTypes? Types { get; set; }

    // True: Enter opens the selection. False: the host gets Enter (Mac style uses Return to rename).
    public bool EnterOpens { get; set; } = true;

    public SortKey SortKey { get; private set; } = SortKey.Name;
    public bool SortDescending { get; private set; }

    // True for the items of a shell folder, whose own order (folders first, then drives) is the right one.
    public bool PreserveOrder { get; set; }

    public ViewMode ViewMode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ApplyMode();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public GroupBy GroupBy
    {
        get => _groupBy;
        set
        {
            if (_groupBy == value) return;
            _groupBy = value;
            Rebuild();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Folders in the icon views show a peek at their files.
    public bool FolderPreviews
    {
        get => _folderPreviews;
        set
        {
            if (_folderPreviews == value) return;
            _folderPreviews = value;
            Rebuild();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // The view or grouping changed (the host remembers it per folder).
    public event EventHandler? ViewChanged;

    // The entries in the order they are shown (group after group when grouped).
    public IReadOnlyList<FileEntry> Entries
    {
        get => _entries;
        set
        {
            _raw = value;
            Rebuild();
        }
    }

    public IReadOnlyList<FileEntry> SelectedEntries => _list.SelectedItems?.OfType<FileEntry>().ToList() ?? [];

    public void SelectAll() => _list.SelectAll();

    // Selects the entries with these names (for tests, and for "select the file I just created").
    public void Select(params string[] names)
    {
        _list.SelectedItems?.Clear();
        foreach (var entry in _entries.Where(e => names.Contains(e.Name)))
            _list.SelectedItems?.Add(entry);
    }

    public ListBox InnerList => _list;

    // The group headings now shown, in order (empty when not grouped).
    public IReadOnlyList<string> GroupTitles => _list.ItemsSource is IEnumerable<object> items ? items.OfType<GroupHeader>().Select(h => h.Title).ToList() : [];

    // Raised for a double click or Enter on the selection; the host decides what opening means.
    public event EventHandler<IReadOnlyList<FileEntry>>? ItemsInvoked;

    // The selection changed (click, keyboard or SelectAll).
    public event EventHandler? SelectionChanged;

    // Raised after the order changed because a heading was clicked.
    public event EventHandler? SortChanged;

    public void Sort(SortKey key, bool descending)
    {
        SortKey = key;
        SortDescending = descending;
        Rebuild();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RequestSort(SortKey key) => Sort(key, key == SortKey && !SortDescending);   // the same heading again flips the direction

    private void InvokeSelected()
    {
        var selected = SelectedEntries;
        if (selected.Count > 0)
            ItemsInvoked?.Invoke(this, selected);
    }

    private void DropHeadingsFromSelection()
    {
        if (_fixingSelection || _list.SelectedItems is not { } selected || !selected.OfType<GroupHeader>().Any()) return;
        _fixingSelection = true;
        try { foreach (var heading in selected.OfType<GroupHeader>().ToList()) selected.Remove(heading); }
        finally { _fixingSelection = false; }
    }

    // ---- laying out ----

    private void Rebuild()
    {
        var groups = FileGrouping.Group(_raw, _groupBy, SortKey, SortDescending, PreserveOrder);
        _entries = groups.SelectMany(g => g.Items).ToList();
        var items = new List<object>();
        foreach (var group in groups)
        {
            if (_groupBy != GroupBy.None) items.Add(new GroupHeader(group.Title, group.Items.Count));
            items.AddRange(group.Items);
        }
        _list.ItemsSource = items;
    }

    private void ApplyMode()
    {
        bool details = _mode == ViewMode.Details;
        _header.IsVisible = details;
        _divider.IsVisible = details;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);   // the wrapped views wrap, the table fits
        _list.ItemsPanel = details
            ? new FuncTemplate<Panel?>(() => new VirtualizingStackPanel())
            : new FuncTemplate<Panel?>(() => new WrapPanel { Margin = new Thickness(6, 0) });
        Rebuild();
    }

    // The heading of a group: its name and count, with a line running out to the right edge. In the wrapped
    // views it is as wide as the list, so it takes a row of its own.
    private Control Heading(GroupHeader heading)
    {
        var title = new TextBlock { Text = $"{heading.Title} ({heading.Count})", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var line = new Border { Height = 1, Background = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray), VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 12, 8, 4), Focusable = false };
        Grid.SetColumn(line, 1);
        grid.Children.Add(title);
        grid.Children.Add(line);
        if (_mode == ViewMode.Details)
            return grid;
        void Fit() => grid.Width = Math.Max(120, _list.Bounds.Width - 40);
        Fit();
        IDisposable? watch = null;
        grid.AttachedToVisualTree += (_, _) => { Fit(); watch = _list.GetObservable(BoundsProperty).Subscribe(new Observer(Fit)); };
        grid.DetachedFromVisualTree += (_, _) => watch?.Dispose();
        return grid;
    }

    private sealed class Observer(Action fit) : IObserver<Rect>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Rect value) => fit();
    }

    private static ColumnDefinitions Columns()
        => new() { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(ModifiedWidth, GridUnitType.Pixel), new ColumnDefinition(TypeWidth, GridUnitType.Pixel), new ColumnDefinition(SizeWidth, GridUnitType.Pixel) };

    private Control Item(FileEntry entry) => _mode switch
    {
        ViewMode.Details => Row(entry),
        ViewMode.Tiles => Tile(entry),
        _ => IconCell(entry, _mode.IconSize()),
    };

    // What to draw for an entry, in every view alike: an item of a shell folder brings its own; a launcher its Icon=;
    // a folder its .directory icon; an image its cached thumbnail; everything else its file type's.
    private IReadOnlyList<string> Names(FileEntry entry, FileTypeInfo? info)
    {
        if (entry.IconNames is not null) return entry.IconNames;
        if (entry.IsDirectory) return FolderIcons(entry.FullPath);
        var names = (info?.IconNames ?? ["text-x-generic"]).ToList();
        if (FileLook.Launcher(entry.FullPath, Vde.Freedesktop.Locale.FromEnvironment()) is { IconNames: { } own })
            names.InsertRange(0, own);
        else if (FileLook.Thumbnail(entry.FullPath) is { } thumb)
            names.Insert(0, thumb);
        return names;
    }

    private static string TypeOf(FileEntry entry, FileTypeInfo? info) => entry.TypeText ?? (entry.IsDirectory ? "Folder" : info?.Description ?? "File");

    private string SizeOf(FileEntry entry, bool virtual_) => entry.IsDirectory || virtual_ && entry.Size == 0 ? "" : ByteFormat.Format(entry.Size);

    private Control Row(FileEntry entry)
    {
        bool virtual_ = entry.IconNames is not null;   // an item of a shell folder, not a file
        var info = entry.IsDirectory || virtual_ ? null : Types?.Describe(entry.FullPath);
        var icon = new ShellIcon { IconNames = Names(entry, info), IconSize = virtual_ ? 32 : 20, Margin = new Thickness(4, 2, 6, 2) };

        var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Control label = name;
        if (entry.Detail is { Length: > 0 } detail)   // a second, smaller line (free space on a drive, where a trashed file came from)
            label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, new TextBlock { Text = detail, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis } } };
        var nameCell = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
        var modified = Cell(entry.ModifiedUtc == default ? "" : entry.ModifiedUtc.ToLocalTime().ToString("g"), 1);
        var type = Cell(TypeOf(entry, info), 2);
        var size = Cell(SizeOf(entry, virtual_), 3);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        size.Margin = new Thickness(8, 2, 14, 2);
        foreach (var secondary in new[] { modified, type, size })
            secondary.Foreground = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        if (entry.IsHidden)
            nameCell.Opacity = 0.6;   // hidden files shown on request look dimmed

        var row = new Grid { ColumnDefinitions = Columns() };
        row.Children.Add(nameCell);
        row.Children.Add(modified);
        row.Children.Add(type);
        row.Children.Add(size);
        return row;
    }

    // XP's tile: a 48 pixel icon, the name, then what the item has to say (type and size, or for a drive the bar and
    // how much is free).
    private Control Tile(FileEntry entry)
    {
        bool virtual_ = entry.IconNames is not null;
        var info = entry.IsDirectory || virtual_ ? null : Types?.Describe(entry.FullPath);
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Usage is { } usage)
            text.Children.Add(new UsageBar { Value = usage, Margin = new Thickness(0, 3), Height = 12 });
        else
            text.Children.Add(new TextBlock { Text = TypeOf(entry, info), FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        var more = entry.Detail is { Length: > 0 } d ? d : SizeOf(entry, virtual_);
        if (more.Length > 0)
            text.Children.Add(new TextBlock { Text = more, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Width = 250, Margin = new Thickness(2) };
        var icon = new ShellIcon { IconNames = Names(entry, info), IconSize = IconSizes.Large, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        if (entry.IsHidden) grid.Opacity = 0.6;
        return grid;
    }

    // An icon view's cell: the picture centred, the name under it (two lines at most), and a drive's bar.
    private Control IconCell(FileEntry entry, int size)
    {
        bool virtual_ = entry.IconNames is not null;
        var info = entry.IsDirectory || virtual_ ? null : Types?.Describe(entry.FullPath);
        Control picture = entry.IsDirectory && !virtual_ && _folderPreviews && size >= IconSizes.Large && FolderIcon.Read(entry.FullPath) is null   // a folder that names its own icon shows that, not a peek
            ? new FolderPreview(entry.FullPath, size, FolderIcons(entry.FullPath), Types)
            : new ShellIcon { IconNames = Names(entry, info), IconSize = size };
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        var cell = new StackPanel { Width = Math.Max(84, size + 28), Margin = new Thickness(2), Spacing = 3 };
        cell.Children.Add(picture);
        cell.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Usage is { } usage)
            cell.Children.Add(new UsageBar { Value = usage, Height = 10, Margin = new Thickness(6, 0) });
        if (entry.IsHidden) cell.Opacity = 0.6;
        return cell;
    }

    private static IReadOnlyList<string> FolderIcons(string folder) => FolderIcon.Read(folder) is { } custom ? [custom, "folder"] : ["folder"];

    private static TextBlock Cell(string text, int column)
    {
        var cell = new TextBlock { Text = text, Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(cell, column);
        return cell;
    }
}
