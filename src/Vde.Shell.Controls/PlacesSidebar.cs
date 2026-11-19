using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Vde.Shell;

namespace Vde.Shell.Controls;

// The list of places down the left of a file manager window. Windows 11 style: tall rows whose
// selection is a rounded pill on the Mica background.
public sealed class PlacesSidebar : UserControl
{
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Background = Avalonia.Media.Brushes.Transparent, Margin = new Avalonia.Thickness(8) };
    private IReadOnlyList<Place> _places = [];
    private bool _quiet;

    public PlacesSidebar()
    {
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Place>((place, _) =>
            place is null ? null : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new ShellIcon { IconNames = place.IconNames, IconSize = 18 }, new TextBlock { Text = place.Name, VerticalAlignment = VerticalAlignment.Center } },
            });
        _list.SelectionChanged += (_, _) =>
        {
            if (!_quiet && _list.SelectedItem is Place place)
                PlaceInvoked?.Invoke(this, place);
        };
        var rows = new Style(x => x.OfType<ListBoxItem>());
        rows.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Avalonia.Thickness(10, 7)));
        rows.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Avalonia.Thickness(2, 1)));
        rows.Setters.Add(new Setter(ListBoxItem.CornerRadiusProperty, new Avalonia.CornerRadius(4)));
        Styles.Add(rows);
        Content = _list;
    }

    public IReadOnlyList<Place> Places
    {
        get => _places;
        set { _places = value; _list.ItemsSource = value; }
    }

    public Place? SelectedPlace
    {
        get => _list.SelectedItem as Place;
        set => _list.SelectedItem = value;
    }

    // The user picked a place; the host decides how to show it (a folder, the drives page, the trash).
    public event EventHandler<Place>? PlaceInvoked;

    // Highlights the place whose folder is the current one, without raising PlaceInvoked.
    public void ShowCurrent(string path)
    {
        var match = _places.FirstOrDefault(p => p.Path == path);
        _quiet = true;
        try { _list.SelectedItem = match; }
        finally { _quiet = false; }
    }
}
