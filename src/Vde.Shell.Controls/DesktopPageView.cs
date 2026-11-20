using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// The Desktop settings page: a preview of the desktop, the installed wallpapers to pick from (or "Browse" for any
// image, or none), how the picture is placed, the background colour and whether the icons show. Every change is
// saved to HKCU\Software\Vde\Desktop at once, and the desktop window follows.
public sealed class DesktopPageView : UserControl
{
    public const string PageId = "desktop";

    private readonly IHive _hive;
    private readonly IHive? _defaults;
    private readonly IReadOnlyList<WallpaperInfo> _wallpapers;
    private readonly Func<Window?, Task<string?>>? _browse;
    private readonly Border _preview = new() { Width = 320, Height = 180, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
    private readonly WrapPanel _gallery = new();
    private readonly ComboBox _mode = new() { ItemsSource = Enum.GetValues<WallpaperMode>() };
    private static readonly (string Label, int Size)[] IconChoices = [("Small (32)", 32), ("Medium (48)", 48), ("Large (64)", 64), ("Extra large (96)", 96), ("Huge (128)", 128)];
    private readonly ComboBox _iconSize = new() { ItemsSource = IconChoices.Select(c => c.Label).ToList() };
    private readonly TextBox _color = new() { PlaceholderText = "#RRGGBB", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _icons = new() { Content = "Show icons on the desktop" };
    private readonly TextBlock _colorError = new() { FontSize = 12, IsVisible = false };
    private bool _updating;

    // browse shows a file picker for the owner window and returns the chosen image, or null.
    public DesktopPageView(IHive hive, IReadOnlyList<WallpaperInfo> wallpapers, Func<Window?, Task<string?>>? browse = null, IHive? systemDefaults = null)
    {
        _hive = hive;
        _defaults = systemDefaults;
        _wallpapers = wallpapers;
        _browse = browse;

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Desktop", FontSize = 24, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_preview);
        panel.Children.Add(Row("Background", _gallery));
        var browseButton = new Button { Content = "Browse…", IsEnabled = browse is not null };
        browseButton.Click += async (_, _) => await BrowseAsync();
        panel.Children.Add(browseButton);
        panel.Children.Add(Row("Picture placement", _mode));
        panel.Children.Add(Row("Desktop icon size", _iconSize));
        panel.Children.Add(Row("Background colour", new StackPanel { Spacing = 4, Children = { _color, _colorError } }));
        panel.Children.Add(_icons);
        Content = new ScrollViewer { Content = panel };

        _mode.SelectionChanged += (_, _) => { if (!_updating && _mode.SelectedItem is WallpaperMode m) Change(s => s with { Mode = m }); };
        _iconSize.SelectionChanged += (_, _) => { if (!_updating && _iconSize.SelectedIndex is >= 0 and var i) Change(s => s with { IconSize = IconChoices[i].Size }); };
        _icons.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { ShowIcons = _icons.IsChecked == true }); };
        _color.TextChanged += (_, _) => { if (!_updating) SetColor(_color.Text ?? ""); };
        Reload();
    }

    public DesktopSettings Settings => DesktopSettingsStore.Load(_hive, _defaults);
    public Border Preview => _preview;
    // The wallpaper tiles (and the "None" tile first): for tests and screen readers.
    public IReadOnlyList<Button> Tiles => _gallery.Children.OfType<Button>().ToList();
    public string? ColorError => _colorError.IsVisible ? _colorError.Text : null;

    public void SelectWallpaper(string? path) => Change(s => s with { Wallpaper = path });
    public void SetMode(WallpaperMode mode) => Change(s => s with { Mode = mode });
    public void SetIconSize(int size) => Change(s => s with { IconSize = size });
    public void SetShowIcons(bool show) => Change(s => s with { ShowIcons = show });

    // A colour that is not #RRGGBB is refused (and the field says so); the saved colour stays as it was.
    public bool SetColor(string text)
    {
        var ok = System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$");
        _colorError.IsVisible = !ok;
        _colorError.Text = "Use the form #RRGGBB, for example #1E1E2E.";
        if (ok) Change(s => s with { BackgroundColor = text.ToUpperInvariant() });
        return ok;
    }

    public async Task BrowseAsync()
    {
        if (_browse is null) return;
        if (await _browse(TopLevel.GetTopLevel(this) as Window) is { } path)
            SelectWallpaper(path);
    }

    private void Change(Func<DesktopSettings, DesktopSettings> edit)
    {
        DesktopSettingsStore.Save(_hive, edit(DesktopSettingsStore.Load(_hive, _defaults)));
        Reload();
    }

    private void Reload()
    {
        var settings = DesktopSettingsStore.Load(_hive, _defaults);
        _updating = true;
        _preview.Background = WallpaperBrush.Create(settings);
        _preview.BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray);
        _mode.SelectedItem = settings.Mode;
        _iconSize.SelectedIndex = Array.FindIndex(IconChoices, c => c.Size == settings.IconSize);
        if (!_color.IsFocused) _color.Text = settings.BackgroundColor;
        _icons.IsChecked = settings.ShowIcons;
        _updating = false;
        RebuildGallery(settings);
    }

    private void RebuildGallery(DesktopSettings current)
    {
        _gallery.Children.Clear();
        _gallery.Children.Add(Tile("None", null, null, current.Wallpaper is null));
        var shown = _wallpapers.ToList();
        if (current.Wallpaper is { } chosen && shown.All(w => w.Path != chosen))
            shown.Insert(0, new WallpaperInfo(Path.GetFileNameWithoutExtension(chosen), chosen));   // a picture from Browse stays visible
        foreach (var w in shown)
            _gallery.Children.Add(Tile(w.Name, w.Path, Thumbnail(w.Path), w.Path == current.Wallpaper));
    }

    private Button Tile(string name, string? path, IImage? image, bool selected)
    {
        var picture = new Border { Width = 96, Height = 54, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = ThemeTokens.Find(this, ThemeToken.Track, Brushes.DimGray) };
        if (image is not null) picture.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
        else if (path is null) picture.Child = new TextBlock { Text = "—", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Tag = name, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(4), Background = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            BorderBrush = selected ? ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue) : Brushes.Transparent,
            Content = new StackPanel { Spacing = 2, Children = { picture, new TextBlock { Text = name, FontSize = 11, MaxWidth = 96, TextTrimming = TextTrimming.CharacterEllipsis } } },
        };
        button.Click += (_, _) => SelectWallpaper(path);
        ToolTip.SetTip(button, path ?? "A solid colour");
        return button;
    }

    // Small, so a folder of 4K wallpapers does not cost memory for the gallery.
    private static Bitmap? Thumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 192);
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Control Row(string label, Control control)
        => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, control } };
}
