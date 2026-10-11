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

    private readonly List<ThemeInfo> _themes = new();

    public IReadOnlyList<ThemeInfo> Themes => _themes;
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public static ThemeInfo Fluent { get; } = new() { Id = FluentId, Name = "Fluent", Base = ThemeBase.Fluent, Comment = "Avalonia's Fluent theme" };
    public static ThemeInfo Simple { get; } = new() { Id = SimpleId, Name = "Simple", Base = ThemeBase.Simple, Comment = "Avalonia's Simple theme" };

    public static ThemeCatalog Discover(XdgDirectories? xdg = null)
    {
        xdg ??= XdgDirectories.Default;
        var catalog = new ThemeCatalog();
        var errors = new List<string>();
        catalog._themes.Add(Fluent);
        catalog._themes.Add(Simple);

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
