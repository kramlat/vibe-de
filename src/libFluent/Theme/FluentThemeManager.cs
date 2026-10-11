using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Avalonia.Themes.Default;

namespace libFluent.Theme;

public enum FluentThemeKind {
    Fluent,
    Simple,
    Default,
    Custom
}

public static class FluentThemeManager {
    private static IStyle? _currentTheme;

    public static void ApplyTheme(FluentThemeKind kind, bool darkMode = false, string? customPath = null) {
        FluentAppHost.EnsureInitialized();

        var app = Application.Current;
        if (app is null)
            return;

        // Remove old theme
        if (_currentTheme != null)
            app.Styles.Remove(_currentTheme);

        switch (kind) {
            case FluentThemeKind.Fluent:
                _currentTheme = new FluentTheme {
                    Mode = darkMode ? FluentThemeMode.Dark : FluentThemeMode.Light
                };
                break;

            case FluentThemeKind.Simple:
                _currentTheme = new SimpleTheme {
                    Mode = darkMode ? SimpleThemeMode.Dark : SimpleThemeMode.Light
                };
                break;

            case FluentThemeKind.Default:
                _currentTheme = new DefaultTheme();
                break;

            case FluentThemeKind.Custom:
                if (customPath is not null) {
                    _currentTheme = new Styles {
                        new StyleInclude(new Uri("avares://libFluent/")) {
                            Source = new Uri($"file://{customPath}")
                        }
                    };
                }
                break;
        }

        if (_currentTheme != null)
            app.Styles.Add(_currentTheme);
    }

    public static void SetAccent(Color accent) {
        if (_currentTheme is FluentTheme fluent)
            fluent.Palette = new FluentPalette { AccentColor = accent };
    }
}
