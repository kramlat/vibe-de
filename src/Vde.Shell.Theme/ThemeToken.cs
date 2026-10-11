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

    // Looks the token up for the element's current theme variant; the fallback is for apps with no engine running.
    public static IBrush Find(Control element, ThemeToken token, IBrush fallback)
        => element.TryFindResource(Key(token), element.ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
