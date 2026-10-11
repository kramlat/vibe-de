using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace libFluent.Theme;

public static class FluentThemeManager {
    private static FluentTheme? _theme;
    private static IStyle? _custom;

    public static void ApplyTheme(bool darkMode = false) {
        FluentAppHost.EnsureInitialized();

        var app = Application.Current;
        if (app is null)
            return;

        if (_theme is null) {
            _theme = new FluentTheme();
            app.Styles.Add(_theme);
        }
        app.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // Layers an extra style file (avares:// URI) over the base theme, replacing any earlier one.
    public static void SetCustomStyle(Uri source) {
        var app = Application.Current;
        if (app is null)
            return;

        if (_custom is not null)
            app.Styles.Remove(_custom);
        _custom = new StyleInclude(source) { Source = source };
        app.Styles.Add(_custom);
    }

    public static void SetAccent(Color accent) {
        if (_theme is null)
            return;

        _theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
        _theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent };
    }
}
