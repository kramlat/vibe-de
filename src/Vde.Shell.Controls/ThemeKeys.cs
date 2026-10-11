namespace Vde.Shell.Controls;

// The Avalonia Fluent resource names the controls draw with. They live in one place because the theme
// assembly will replace them with stable semantic tokens that every theme (Fluent, Simple, custom) maps.
internal static class ThemeKeys
{
    public const string TextSecondary = "SystemControlForegroundBaseMediumBrush";
    public const string HoverFill = "SystemControlHighlightListLowBrush";
    public const string SelectedFill = "SystemControlHighlightListMediumBrush";
    public const string Accent = "SystemControlHighlightAccentBrush";
    public const string Critical = "SystemControlErrorTextForegroundBrush";
    public const string Track = "SystemControlBackgroundBaseLowBrush";

    public static readonly string[] All = [TextSecondary, HoverFill, SelectedFill, Accent, Critical, Track];
}
