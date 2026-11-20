using Avalonia.Media;
using Vde.Freedesktop;

namespace Vde.Shell.Theme;

// The themes that can be chosen: the built-in Fluent and Simple, plus every user or system theme found in
// <data dir>/vde/themes/<id>/index.theme. User themes shadow system ones with the same id.
//
//   [VDE Theme]
//   Name=Midnight
//   Comment=...                 (optional)
//   Author=...                  (optional)
//   Base=Fluent                 Fluent, Simple or None (default Fluent)
//   Variant=Dark                System (default), Light or Dark: pins the variant
//   Accent=#ff6a00              optional
//
//   [Tokens]                    colours for both variants; names are the ThemeToken names
//   TextSecondary=#99ffffff
//   [Tokens Light]              per-variant colours win over [Tokens]
//   [Tokens Dark]
public sealed class ThemeCatalog
{
    public const string FluentId = "fluent";
    public const string SimpleId = "simple";
    public const string BreezeId = "breeze";
    public const string RedmondId = "redmond";

    private readonly List<ThemeInfo> _themes = new();

    public IReadOnlyList<ThemeInfo> Themes => _themes;
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public static ThemeInfo Fluent { get; } = new() { Id = FluentId, Name = "Fluent", Base = ThemeBase.Fluent, Comment = "Avalonia's Fluent theme" };
    public static ThemeInfo Simple { get; } = new() { Id = SimpleId, Name = "Simple", Base = ThemeBase.Simple, Comment = "Avalonia's Simple theme" };

    // KDE's Breeze look: cool greys, the Breeze blue, slightly rounded corners. Both variants.
    public static ThemeInfo Breeze { get; } = new()
    {
        Id = BreezeId, Name = "Breeze", Base = ThemeBase.Simple, Comment = "The KDE Breeze look: cool greys and Breeze blue",
        Accent = Color.Parse("#3DAEE9"), CornerRadius = 3,
        LightTokens = Tokens("#3DAEE9", "#232629", "#6E7479", "#EFF0F1", "#BCC0C4", "#1A3DAEE9", "#403DAEE9", "#DCDFE1", "#DA4453"),
        DarkTokens = Tokens("#3DAEE9", "#EFF0F1", "#A1A9B1", "#31363B", "#4D4D4D", "#1A3DAEE9", "#403DAEE9", "#4D5257", "#DA4453"),
        LightPalette = Palette("#EFF0F1", "#232629", "#BCC0C4", "#8E949A", "#4C5055", "#FCFCFC", "#E3E5E7", "#D2D5D8", "#BCC0C4", "#3DAEE9", "#FFFFFF"),
        DarkPalette = Palette("#31363B", "#EFF0F1", "#4D4D4D", "#6E7479", "#9DA3A8", "#232629", "#3B4045", "#4D5257", "#5A6066", "#3DAEE9", "#FFFFFF"),
    };

    // The classic Windows look (Windows 2000 and before): grey 3D-less panels, square corners, navy selection.
    // Only the light variant exists, as it did then.
    public static ThemeInfo Redmond { get; } = new()
    {
        Id = RedmondId, Name = "Redmond", Base = ThemeBase.Simple, Variant = VariantPreference.Light, Comment = "The classic Windows look",
        Accent = Color.Parse("#0A246A"), CornerRadius = 0,
        LightTokens = Tokens("#0A246A", "#000000", "#404040", "#D4D0C8", "#808080", "#33000000", "#660A246A", "#808080", "#C00000"),
        DarkTokens = Tokens("#0A246A", "#000000", "#404040", "#D4D0C8", "#808080", "#33000000", "#660A246A", "#808080", "#C00000"),
        LightPalette = Palette("#D4D0C8", "#000000", "#808080", "#404040", "#000000", "#FFFFFF", "#D4D0C8", "#E4E0D8", "#808080", "#0A246A", "#FFFFFF"),
        DarkPalette = Palette("#D4D0C8", "#000000", "#808080", "#404040", "#000000", "#FFFFFF", "#D4D0C8", "#E4E0D8", "#808080", "#0A246A", "#FFFFFF"),
    };

    // accent, text, text2, surface, border, hover, selected, track, critical
    private static Dictionary<ThemeToken, Color> Tokens(string accent, string text, string text2, string surface, string border, string hover, string selected, string track, string critical) => new()
    {
        [ThemeToken.Accent] = Color.Parse(accent), [ThemeToken.TextPrimary] = Color.Parse(text), [ThemeToken.TextSecondary] = Color.Parse(text2),
        [ThemeToken.Surface] = Color.Parse(surface), [ThemeToken.Border] = Color.Parse(border), [ThemeToken.HoverFill] = Color.Parse(hover),
        [ThemeToken.SelectedFill] = Color.Parse(selected), [ThemeToken.Track] = Color.Parse(track), [ThemeToken.Critical] = Color.Parse(critical),
    };

    // window, text, borderLow, borderMid, borderHigh, view (text boxes, lists), control, controlMid, controlHigh, highlight, highlightText
    private static Dictionary<string, Color> Palette(string window, string text, string borderLow, string borderMid, string borderHigh,
        string view, string control, string controlMid, string controlHigh, string highlight, string highlightText) => new()
    {
        ["ThemeBackground"] = Color.Parse(window), ["ThemeForeground"] = Color.Parse(text),
        ["ThemeBorderLow"] = Color.Parse(borderLow), ["ThemeBorderMid"] = Color.Parse(borderMid), ["ThemeBorderHigh"] = Color.Parse(borderHigh),
        ["ThemeControlLow"] = Color.Parse(view), ["ThemeControlMid"] = Color.Parse(control), ["ThemeControlMidHigh"] = Color.Parse(controlMid),
        ["ThemeControlHigh"] = Color.Parse(controlHigh), ["ThemeControlHighlightLow"] = Color.Parse(control),
        ["ThemeControlHighlightMid"] = Color.Parse(controlMid), ["ThemeControlHighlightHigh"] = Color.Parse(controlHigh),
        ["Highlight"] = Color.Parse(highlight), ["HighlightForeground"] = Color.Parse(highlightText), ["ThemeAccent"] = Color.Parse(highlight),
    };

    public static ThemeCatalog Discover(XdgDirectories? xdg = null)
    {
        xdg ??= XdgDirectories.Default;
        var catalog = new ThemeCatalog();
        var errors = new List<string>();
        catalog._themes.Add(Fluent);
        catalog._themes.Add(Simple);
        catalog._themes.Add(Breeze);
        catalog._themes.Add(Redmond);

        foreach (var dataDir in xdg.DataSearchPath)
        {
            var root = Path.Combine(dataDir, "vde", "themes");
            if (!Directory.Exists(root))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(dir).ToLowerInvariant();
                var file = Path.Combine(dir, "index.theme");
                if (!File.Exists(file) || catalog.Find(id) is not null)
                    continue;   // not a theme, or a higher-priority one with this id exists (built-ins included)

                try
                {
                    catalog._themes.Add(Parse(id, dir, IniFile.Load(file), errors));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add($"{file}: {e.Message}");
                }
            }
        }

        catalog.Errors = errors.Distinct().ToList();   // a bad shared token is seen once per variant
        return catalog;
    }

    public ThemeInfo? Find(string id) => _themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    // The requested theme, or Fluent when it is unknown (a deleted theme must not leave the desktop unstyled).
    public ThemeInfo FindOrDefault(string id) => Find(id) ?? Fluent;

    private static ThemeInfo Parse(string id, string directory, IniFile ini, List<string> errors)
    {
        const string group = "VDE Theme";
        if (!ini.HasGroup(group))
            throw new InvalidDataException("Missing [VDE Theme] group.");

        var location = Path.Combine(directory, "index.theme");
        ThemeBase themeBase = ThemeBase.Fluent;
        if (ini.Get(group, "Base") is { } baseName && !Enum.TryParse(baseName, true, out themeBase))
        {
            errors.Add($"{location}: unknown Base '{baseName}', using Fluent.");
            themeBase = ThemeBase.Fluent;
        }

        var variant = VariantPreference.System;
        if (ini.Get(group, "Variant") is { } variantName && !Enum.TryParse(variantName, true, out variant))
        {
            errors.Add($"{location}: unknown Variant '{variantName}', following the system.");
            variant = VariantPreference.System;
        }

        Color? accent = null;
        if (ini.Get(group, "Accent") is { } accentText)
        {
            if (Color.TryParse(accentText, out var parsed))
                accent = parsed;
            else
                errors.Add($"{location}: bad Accent '{accentText}'.");
        }

        return new ThemeInfo
        {
            Id = id,
            Name = ini.Get(group, "Name") ?? id,
            Comment = ini.Get(group, "Comment"),
            Author = ini.Get(group, "Author"),
            Base = themeBase,
            Variant = variant,
            Accent = accent,
            LightTokens = ReadTokens(ini, "Tokens Light", ini, location, errors),
            DarkTokens = ReadTokens(ini, "Tokens Dark", ini, location, errors),
            Directory = directory,
        };
    }

    // Shared [Tokens] first, then the variant's own group overriding it.
    private static Dictionary<ThemeToken, Color> ReadTokens(IniFile ini, string variantGroup, IniFile _, string location, List<string> errors)
    {
        var tokens = new Dictionary<ThemeToken, Color>();
        foreach (var group in new[] { "Tokens", variantGroup })
            foreach (var token in Enum.GetValues<ThemeToken>())
                if (ini.Get(group, token.ToString()) is { } text)
                {
                    if (Color.TryParse(text, out var color))
                        tokens[token] = color;
                    else
                        errors.Add($"{location}: [{group}] {token}: bad colour '{text}'.");
                }
        return tokens;
    }
}
