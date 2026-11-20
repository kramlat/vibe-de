using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// Personalization > Colors, laid out like Windows 11's page: a preview, the mode, transparency, the accent
// colour (Automatic follows the theme; Manual shows recent colours, the palette and a field for any #RRGGBB)
// and where the accent is used. Everything is saved to HKCU\Software\Vde\Appearance at once; the shell's
// ThemeEngine and the taskbar follow.
public sealed class ColorsPageView : UserControl
{
    public const string PageId = "colors";

    // The accent palette: nine to a row, in the order Windows lays its own out.
    public static readonly IReadOnlyList<string> Palette =
    [
        "#FFB900", "#FF8C00", "#F7630C", "#CA5010", "#DA3B01", "#EF6950", "#D13438", "#FF4343", "#E74856",
        "#E81123", "#EA005E", "#C30052", "#E3008C", "#BF0077", "#C239B3", "#9A0089", "#0078D7", "#0063B1",
        "#8E8CD8", "#6B69D6", "#8764B8", "#744DA9", "#B146C2", "#881798", "#0099BC", "#2D7D9A", "#00B7C3",
        "#038387", "#00B294", "#018574", "#00CC6A", "#10893E", "#7A7574", "#5D5A58", "#68768A", "#515C6B",
        "#567C73", "#486860", "#498205", "#107C10", "#767676", "#4C4A48", "#69797E", "#4A5459", "#647C64",
        "#525E54", "#847545", "#7E735F",
    ];

    private static readonly (string Label, VariantPreference Value)[] Modes =
        [("Light", VariantPreference.Light), ("Dark", VariantPreference.Dark), ("Use system setting", VariantPreference.System)];

    private readonly IHive _hive;
    private readonly IHive? _defaults;
    private readonly Border _preview = new() { Width = 320, Height = 180, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
    private readonly ComboBox _mode = new() { MinWidth = 160, ItemsSource = Modes.Select(m => m.Label).ToList() };
    private readonly ToggleSwitch _transparency = new() { OnContent = "On", OffContent = "Off" };
    private readonly ComboBox _accentMode = new() { MinWidth = 130, ItemsSource = new[] { "Automatic", "Manual" } };
    private readonly StackPanel _accentBody = new() { Spacing = 14, Margin = new Thickness(28, 8, 12, 12) };
    private readonly WrapPanel _recent = new();
    private readonly WrapPanel _palette = new() { Width = 9 * 54 };
    private readonly TextBox _custom = new() { PlaceholderText = "#RRGGBB", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _customError = new() { FontSize = 12, IsVisible = false, Text = "Use the form #RRGGBB, for example #1E1E2E." };
    private readonly StackPanel _customBody = new() { Spacing = 4, IsVisible = false };
    private readonly ToggleSwitch _onTaskbar = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _onTitleBars = new() { OnContent = "On", OffContent = "Off" };
    private bool _updating;

    // hive is the user's registry; systemDefaults only supplies the wallpaper the preview shows.
    public ColorsPageView(IHive hive, IHive? systemDefaults = null)
    {
        _hive = hive;
        _defaults = systemDefaults;

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Colors", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(_preview);
        _preview.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(SettingRow("Choose your mode", "Change the colors that appear in Windows and your apps", _mode));
        panel.Children.Add(SettingRow("Transparency effects", "Windows and surfaces appear translucent", _transparency));

        var accentHead = SettingRow("Accent color", null, _accentMode);
        _accentBody.Children.Add(Section("Recent colors", _recent));
        _accentBody.Children.Add(Section("Windows colors", _palette));
        var view = new Button { Content = "View colors", Tag = "view-colors" };
        view.Click += (_, _) => _customBody.IsVisible = !_customBody.IsVisible;
        _custom.TextChanged += (_, _) => { if (!_updating && (_custom.Text ?? "").Length == 7) ApplyCustom(); };
        _customBody.Children.Add(_custom);
        _customBody.Children.Add(_customError);
        panel.Children.Add(accentHead);
        panel.Children.Add(new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Child = _accentBody },
                    Row("Custom colors", null, new StackPanel { Spacing = 8, Children = { view, _customBody } }, indent: true),
                    Row("Show accent color on Start and taskbar", null, _onTaskbar, indent: true),
                    Row("Show accent color on title bars and windows borders", null, _onTitleBars, indent: true),
                },
            },
        });
        Content = new ScrollViewer { Content = panel };

        _mode.SelectionChanged += (_, _) => { if (!_updating && _mode.SelectedIndex >= 0) Change(s => s with { Variant = Modes[_mode.SelectedIndex].Value }); };
        _transparency.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { Transparency = _transparency.IsChecked == true }); };
        _onTaskbar.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { AccentOnTaskbar = _onTaskbar.IsChecked == true }); };
        _onTitleBars.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { AccentOnTitleBars = _onTitleBars.IsChecked == true }); };
        _accentMode.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            if (_accentMode.SelectedIndex == 0) Change(s => s with { Accent = null });
            else if (Selection.Accent is null) SetAccent(Color.Parse(Palette[16]));   // Manual starts from the standard blue
        };
        _hive.Changed += OnHiveChanged;
        Reload();
    }

    public ThemeSelection Selection => ThemeSettings.Load(_hive);
    public Border Preview => _preview;
    public ComboBox ModeBox => _mode;
    public ToggleSwitch TransparencyToggle => _transparency;
    public ComboBox AccentModeBox => _accentMode;
    public ToggleSwitch TaskbarToggle => _onTaskbar;
    public ToggleSwitch TitleBarsToggle => _onTitleBars;
    // The palette's swatches in order, and the recent colours' (newest first): for tests and screen readers.
    public IReadOnlyList<Button> PaletteSwatches => _palette.Children.OfType<Button>().ToList();
    public IReadOnlyList<Button> RecentSwatches => _recent.Children.OfType<Button>().ToList();
    public string? CustomError => _customError.IsVisible ? _customError.Text : null;
    public bool AccentChoicesVisible => _accentBody.IsVisible;

    public void SetMode(VariantPreference mode) => Change(s => s with { Variant = mode });

    // Picks a colour: it becomes the accent (Manual) and the first of the recent colours.
    public void SetAccent(Color color) => Change(s => s with { Accent = color, RecentAccents = ThemeSettings.WithRecent(s.RecentAccents, color) });

    public bool SetCustom(string text)
    {
        var ok = System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$");
        _customError.IsVisible = !ok;
        if (ok) SetAccent(Color.Parse(text));
        return ok;
    }

    private void ApplyCustom() => SetCustom(_custom.Text ?? "");

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hive.Changed -= OnHiveChanged;
    }

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(ThemeSettings.KeyPath))
            Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    private void Change(Func<ThemeSelection, ThemeSelection> edit)
    {
        ThemeSettings.Save(_hive, edit(ThemeSettings.Load(_hive)));
        Reload();
    }

    private void Reload()
    {
        var s = ThemeSettings.Load(_hive);
        _updating = true;
        try
        {
            _mode.SelectedIndex = Array.FindIndex(Modes, m => m.Value == s.Variant);
            _transparency.IsChecked = s.Transparency;
            _accentMode.SelectedIndex = s.Accent is null ? 0 : 1;
            _accentBody.IsVisible = s.Accent is not null;
            _onTaskbar.IsChecked = s.AccentOnTaskbar;
            _onTitleBars.IsChecked = s.AccentOnTitleBars;
            if (!_custom.IsFocused) _custom.Text = s.Accent?.ToString().Replace("#FF", "#", StringComparison.OrdinalIgnoreCase).ToUpperInvariant() ?? "";
        }
        finally { _updating = false; }

        _recent.Children.Clear();
        foreach (var color in s.RecentAccents ?? [])
            _recent.Children.Add(Swatch(color, s.Accent));
        _palette.Children.Clear();
        foreach (var hex in Palette)
            _palette.Children.Add(Swatch(Color.Parse(hex), s.Accent));
        RefreshPreview(s);
    }

    private void RefreshPreview(ThemeSelection s)
    {
        var dark = s.Variant == VariantPreference.Dark
            || (s.Variant == VariantPreference.System && ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        var accent = s.Accent ?? Color.Parse("#0078D7");
        IBrush wallpaper;
        try { wallpaper = WallpaperBrush.Create(DesktopSettingsStore.Load(_hive, _defaults)); }
        catch (Exception) { wallpaper = new SolidColorBrush(Color.Parse("#1F3A5F")); }
        _preview.Background = wallpaper;
        _preview.BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray);
        _preview.Child = ThemePreview.MockWindow(accent, dark, s.AccentOnTitleBars);
    }

    private Button Swatch(Color color, Color? selected)
    {
        var isSelected = selected == color;
        var chip = new Border
        {
            Width = 48, Height = 48, Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(2),
            BorderBrush = isSelected ? Brushes.White : Brushes.Transparent,
            Child = isSelected ? new TextBlock { Text = "✓", Foreground = Brushes.White, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0) } : null,
        };
        var button = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = chip, Tag = color.ToString() };
        ToolTip.SetTip(button, color.ToString().Replace("#FF", "#", StringComparison.OrdinalIgnoreCase).ToUpperInvariant());
        button.Click += (_, _) => SetAccent(color);
        return button;
    }

    private Control Section(string title, Control body)
        => new StackPanel { Spacing = 8, Children = { new TextBlock { Text = title, FontSize = 12 }, body } };

    private Control SettingRow(string title, string? help, Control control)
        => Row(title, help, control, indent: false);

    private Control Row(string title, string? help, Control control, bool indent)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(indent ? 28 : 0, 0, 12, 0) };
        texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (help is not null)
            texts.Children.Add(new TextBlock { Text = help, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(texts);
        control.SetValue(Grid.ColumnProperty, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Border
        {
            Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent), Child = grid,
        };
    }
}
