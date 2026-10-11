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

    public string? Directory { get; init; }
    public bool IsBuiltIn => Directory is null;
}
