using Avalonia.Media;
using Vibe.Registry;

namespace Vde.Shell.Theme;

public sealed record ThemeSelection(
    string ThemeId = ThemeCatalog.FluentId,
    VariantPreference Variant = VariantPreference.System,
    Color? Accent = null);

// The user's choice, kept in the registry (HKCU\Software\Vde\Appearance) so Settings, the shell and
// any other program see the same thing, and changing it re-themes everything that is listening.
public static class ThemeSettings
{
    public const string KeyPath = @"Software\Vde\Appearance";

    public static ThemeSelection Load(IHive hive)
    {
        var id = hive.GetValue(KeyPath, "Theme") is { Kind: RegistryValueKind.String } t && t.Data is string { Length: > 0 } s ? s : ThemeCatalog.FluentId;

        var variant = VariantPreference.System;
        if (hive.GetValue(KeyPath, "Variant") is { Kind: RegistryValueKind.String } v && v.Data is string vs)
            Enum.TryParse(vs, true, out variant);

        Color? accent = null;
        if (hive.GetValue(KeyPath, "Accent") is { Kind: RegistryValueKind.String } a && a.Data is string accentText
            && Color.TryParse(accentText, out var parsed))
            accent = parsed;

        return new ThemeSelection(id, variant, accent);
    }

    public static void Save(IHive hive, ThemeSelection selection)
    {
        hive.SetValue(KeyPath, "Theme", RegistryValueKind.String, selection.ThemeId);
        hive.SetValue(KeyPath, "Variant", RegistryValueKind.String, selection.Variant.ToString());
        if (selection.Accent is { } accent)
            hive.SetValue(KeyPath, "Accent", RegistryValueKind.String, accent.ToString());
        else
            hive.DeleteValue(KeyPath, "Accent");
    }
}
