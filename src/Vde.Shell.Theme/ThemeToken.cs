using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Vde.Shell.Theme;

// The colours the shell draws with. Controls ask for these by name; every theme supplies them, so no
// control depends on the resource names of Fluent, Simple or any custom theme.
public enum ThemeToken
{
    Accent,
    TextPrimary,
    TextSecondary,
    Surface,
    Border,
    HoverFill,
    SelectedFill,
    Track,
    Critical,
}

public static class ThemeTokens
{
    // Raised whenever a ThemeEngine installs or removes a theme. A light/dark switch also raises the
    // element's ActualThemeVariantChanged, but swapping Fluent for Simple at the same variant does not,
    // so controls that cache theme colours listen to this too (subscribe while attached, unsubscribe on detach).
    public static event EventHandler? Changed;

    internal static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // The resource key a token is published under in the application's resources.
    public static string Key(ThemeToken token) => "Vde." + token;

    // True under a classic style (Redmond): the shell's own titlebars and caption buttons draw the old way.
    public static bool IsClassic(Control element)
        => element.TryFindResource("Vde.Classic", element.ActualThemeVariant, out var value) && value is true;

    // The Colors page's "Show accent color on title bars and window borders": the active window's titlebar and
    // border take the accent. Never under a classic style, which has its own gradient bar.
    public static bool AccentOnTitleBars(Control element)
        => element.TryFindResource("Vde.AccentOnTitleBars", element.ActualThemeVariant, out var value) && value is true;

    public static bool OnAccentBar(Control element, bool active) => active && !IsClassic(element) && AccentOnTitleBars(element);

    // Black or white, whichever reads better on the colour (WCAG relative luminance).
    public static Color InkOn(Color background)
    {
        static double Lin(byte v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Lin(background.R) + 0.7152 * Lin(background.G) + 0.0722 * Lin(background.B);
        return luminance > 0.4 ? Colors.Black : Colors.White;
    }

    public static Color AccentColor(Control element)
        => (Find(element, ThemeToken.Accent, Brushes.DodgerBlue) as ISolidColorBrush)?.Color ?? Colors.DodgerBlue;

    // Looks the token up for the element's current theme variant; the fallback is for apps with no engine running.
    public static IBrush Find(Control element, ThemeToken token, IBrush fallback)
        => element.TryFindResource(Key(token), element.ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
