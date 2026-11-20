using Avalonia.Media;

namespace Vde.Shell.Theme;

public enum ThemeBase
{
    Fluent,
    Simple,
    None,     // no Avalonia theme underneath: the theme styles everything itself
}

// What the user wants. System follows the OS light/dark setting.
public enum VariantPreference
{
    System,
    Light,
    Dark,
}

public sealed record ThemeInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Comment { get; init; }
    public string? Author { get; init; }
    public ThemeBase Base { get; init; } = ThemeBase.Fluent;

    // System: follows the user's choice. Light or Dark: the theme only looks right in that variant.
    public VariantPreference Variant { get; init; } = VariantPreference.System;
    public Color? Accent { get; init; }

    // Colours this theme sets itself; anything missing comes from the base theme.
    public IReadOnlyDictionary<ThemeToken, Color> LightTokens { get; init; } = new Dictionary<ThemeToken, Color>();
    public IReadOnlyDictionary<ThemeToken, Color> DarkTokens { get; init; } = new Dictionary<ThemeToken, Color>();

    // Colours for the base theme's own resources, by name without the Color/Brush suffix ("ThemeBackground",
    // "ThemeControlMid", "Highlight"...). Only the Simple base has such resources; others ignore this.
    public IReadOnlyDictionary<string, Color> LightPalette { get; init; } = new Dictionary<string, Color>();
    public IReadOnlyDictionary<string, Color> DarkPalette { get; init; } = new Dictionary<string, Color>();

    // How round the stock controls' corners are; null keeps the shell's Windows 11 value.
    public double? CornerRadius { get; init; }

    // The classic (Windows 2000) look for chrome the shell draws itself: navy gradient titlebars, raised caption buttons.
    public bool Classic { get; init; }
    // The active titlebar of a classic style fades from one colour to the other (null: the Windows 2000 navy to light blue).
    public Color? ClassicBarFrom { get; init; }
    public Color? ClassicBarTo { get; init; }

    public string? Directory { get; init; }
    public bool IsBuiltIn => Directory is null;
}
