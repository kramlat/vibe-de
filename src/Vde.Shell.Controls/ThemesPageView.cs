using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// Personalization > Themes, laid out like Windows 11's page: a preview and a summary of what the current theme
// sets (background, colour, mode, mouse cursor), then the "Current style" card that opens a gallery of every
// installed theme. Choosing one saves it to HKCU\Software\Vde\Appearance and the whole desktop re-themes.
public sealed class ThemesPageView : UserControl
{
    public const string PageId = "themes";

    private readonly IHive _hive;
    private readonly IHive? _defaults;
    private readonly ThemeCatalog _catalog;
    private readonly Border _preview = new() { Width = 335, Height = 188, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly StackPanel _summary = new() { Spacing = 14 };
    private readonly TextBlock _currentName = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _gallery = new() { Margin = new Thickness(20, 12, 12, 16) };
    private readonly Border _galleryHost = new();
    private readonly TextBlock _chevron = new() { Text = "⌃", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private bool _open = true;

    public ThemesPageView(IHive hive, ThemeCatalog catalog, IHive? systemDefaults = null)
    {
        _hive = hive;
        _defaults = systemDefaults;
        _catalog = catalog;

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Themes", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 16) };
        top.Children.Add(_preview);
        _summary.Margin = new Thickness(28, 6, 0, 0);
        _summary.SetValue(Grid.ColumnProperty, 1);
        top.Children.Add(_summary);
        panel.Children.Add(top);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var title = new StackPanel { Spacing = 2, Children =
        {
            new TextBlock { Text = "Current style" },
            new TextBlock { Text = "Choose how windows and controls look: Fluent, Simple, Breeze, Redmond or an installed style", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
        } };
        head.Children.Add(title);
        _currentName.SetValue(Grid.ColumnProperty, 1);
        head.Children.Add(_currentName);
        _chevron.SetValue(Grid.ColumnProperty, 2);
        head.Children.Add(_chevron);
        var headButton = new Button
        {
            Tag = "current-style", Content = head, Padding = new Thickness(14, 12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent),
        };
        headButton.Click += (_, _) => { _open = !_open; _galleryHost.IsVisible = _open; _chevron.Text = _open ? "⌃" : "⌄"; };
        panel.Children.Add(headButton);

        _galleryHost.Child = _gallery;
        panel.Children.Add(_galleryHost);

        Content = new ScrollViewer { Content = panel };
        _hive.Changed += OnHiveChanged;
        Reload();
    }

    public Border Preview => _preview;
    // One button per installed theme, in catalog order, and the one in use (-1 if none): for tests and screen readers.
    public IReadOnlyList<Button> Tiles => _gallery.Children.OfType<Button>().ToList();
    public int SelectedTile => Math.Max(-1, _catalog.Themes.ToList().FindIndex(t => string.Equals(t.Id, ThemeSettings.Load(_hive).ThemeId, StringComparison.OrdinalIgnoreCase)));
    public string CurrentThemeName => _currentName.Text ?? "";

    public void Select(string themeId)
    {
        if (_catalog.Find(themeId) is not { } theme) return;
        // A theme that pins the accent hands it over; one that does not leaves the user's own accent alone.
        ThemeSettings.Save(_hive, ThemeSettings.Load(_hive) with { ThemeId = theme.Id, Variant = theme.Variant is VariantPreference.System ? ThemeSettings.Load(_hive).Variant : theme.Variant });
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(ThemeSettings.KeyPath) || change.IsUnder(@"Software\Vde\Desktop"))
            Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    private void Reload()
    {
        var selection = ThemeSettings.Load(_hive);
        var theme = _catalog.FindOrDefault(selection.ThemeId);
        var desktop = DesktopSettingsStore.Load(_hive, _defaults);
        _currentName.Text = theme.Name;

        _preview.BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray);
        _preview.Background = Wallpaper(desktop);
        _preview.Child = ThemePreview.MockWindow(Accent(selection, theme), IsDark(selection, theme), selection.AccentOnTitleBars, 1.1);

        _summary.Children.Clear();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 40, RowSpacing = 18 };
        Info(grid, 0, 0, "Background", desktop.Wallpaper is { } w ? Path.GetFileNameWithoutExtension(w) : "Solid color");
        Info(grid, 1, 0, "Color", selection.Accent is { } a ? a.ToString().Replace("#FF", "#", StringComparison.OrdinalIgnoreCase).ToUpperInvariant() : "Automatic");
        Info(grid, 0, 1, "Mode", selection.Variant switch { VariantPreference.Light => "Light", VariantPreference.Dark => "Dark", _ => "Use system setting" });
        Info(grid, 1, 1, "Mouse cursor", Environment.GetEnvironmentVariable("XCURSOR_THEME") is { Length: > 0 } c ? c : "Default");
        _summary.Children.Add(grid);

        _gallery.Children.Clear();
        foreach (var t in _catalog.Themes)
            _gallery.Children.Add(Tile(t, desktop, selection, string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase)));
    }

    private static void Info(Grid grid, int column, int row, string label, string value)
    {
        var text = new StackPanel { Children = { new TextBlock { Text = label }, new TextBlock { Text = value, FontSize = 12, Opacity = 0.7 } } };
        text.SetValue(Grid.ColumnProperty, column);
        text.SetValue(Grid.RowProperty, row);
        grid.Children.Add(text);
    }

    private Button Tile(ThemeInfo theme, DesktopSettings desktop, ThemeSelection selection, bool selected)
    {
        var picture = new Border
        {
            Width = 112, Height = 90, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = Wallpaper(desktop),
            Child = ThemePreview.MockWindow(Accent(selection, theme), IsDark(selection, theme), false, 0.45),
        };
        var button = new Button
        {
            Tag = theme.Id, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(3), Background = Brushes.Transparent,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6),
            BorderBrush = selected ? ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue) : Brushes.Transparent,
            Content = picture,
        };
        ToolTip.SetTip(button, theme.Comment is { Length: > 0 } c ? $"{theme.Name} — {c}" : theme.Name);
        button.Click += (_, _) => Select(theme.Id);
        return button;
    }

    private static Color Accent(ThemeSelection selection, ThemeInfo theme) => selection.Accent ?? theme.Accent ?? Color.Parse("#0078D7");

    private bool IsDark(ThemeSelection selection, ThemeInfo theme)
    {
        var variant = theme.Variant is VariantPreference.System ? selection.Variant : theme.Variant;
        return variant == VariantPreference.Dark || (variant == VariantPreference.System && ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
    }

    private static IBrush Wallpaper(DesktopSettings desktop)
    {
        try { return WallpaperBrush.Create(desktop); }
        catch (Exception) { return new SolidColorBrush(Color.Parse("#1F3A5F")); }
    }
}
