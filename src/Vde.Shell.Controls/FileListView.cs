using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// A folder's contents as a details list: icon and name, date modified, type and size, with sortable headers.
// It shows what it is given; navigation, sorting and opening are decisions for whoever hosts it.
public sealed class FileListView : UserControl
{
    private const double ModifiedWidth = 150, TypeWidth = 150, SizeWidth = 90;

    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Grid _header = new() { ColumnDefinitions = Columns() };
    private IReadOnlyList<FileEntry> _entries = [];

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

        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FileEntry>((entry, _) => entry is null ? null : Row(entry));
        _list.DoubleTapped += (_, _) => InvokeSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { InvokeSelected(); e.Handled = true; }
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(_list);
        Content = layout;
    }

    // Used to find each file's icon and description; without it files get generic icons.
    public FileTypes? Types { get; set; }

    public SortKey SortKey { get; private set; } = SortKey.Name;
    public bool SortDescending { get; private set; }

    public IReadOnlyList<FileEntry> Entries
    {
        get => _entries;
        set
        {
            _entries = FolderLister.Sort(value, SortKey, SortDescending);
            _list.ItemsSource = _entries;
        }
    }

    public IReadOnlyList<FileEntry> SelectedEntries => _list.SelectedItems?.OfType<FileEntry>().ToList() ?? [];

    public void SelectAll() => _list.SelectAll();

    // Raised for a double click or Enter on the selection; the host decides what opening means.
    public event EventHandler<IReadOnlyList<FileEntry>>? ItemsInvoked;

    // Raised after the order changed because a heading was clicked.
    public event EventHandler? SortChanged;

    public void Sort(SortKey key, bool descending)
    {
        SortKey = key;
        SortDescending = descending;
        Entries = _entries;
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RequestSort(SortKey key) => Sort(key, key == SortKey && !SortDescending);   // the same heading again flips the direction

    private void InvokeSelected()
    {
        var selected = SelectedEntries;
        if (selected.Count > 0)
            ItemsInvoked?.Invoke(this, selected);
    }

    private static ColumnDefinitions Columns()
        => new() { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(ModifiedWidth, GridUnitType.Pixel), new ColumnDefinition(TypeWidth, GridUnitType.Pixel), new ColumnDefinition(SizeWidth, GridUnitType.Pixel) };

    private Control Row(FileEntry entry)
    {
        var info = entry.IsDirectory ? null : Types?.Describe(entry.FullPath);
        var icon = new ShellIcon { IconNames = entry.IsDirectory ? ["folder"] : info?.IconNames ?? ["text-x-generic"], IconSize = 20, Margin = new Thickness(4, 2, 6, 2) };

        var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var nameCell = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } };
        var modified = Cell(entry.ModifiedUtc.ToLocalTime().ToString("g"), 1);
        var type = Cell(entry.IsDirectory ? "Folder" : info?.Description ?? "File", 2);
        var size = Cell(entry.IsDirectory ? "" : ByteFormat.Format(entry.Size), 3);
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

    private static TextBlock Cell(string text, int column)
    {
        var cell = new TextBlock { Text = text, Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(cell, column);
        return cell;
    }
}
