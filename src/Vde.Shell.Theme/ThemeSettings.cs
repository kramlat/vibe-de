using Avalonia.Media;
using Vibe.Registry;

namespace Vde.Shell.Theme;

public sealed record ThemeSelection(
    string ThemeId = ThemeCatalog.FluentId,
    VariantPreference Variant = VariantPreference.System,
    Color? Accent = null,
    bool Transparency = true,            // Mica and other translucent surfaces
    bool AccentOnTaskbar = false,        // the taskbar and Start menu are filled with the accent
    bool AccentOnTitleBars = false,      // title bars and window borders take the accent
    IReadOnlyList<Color>? RecentAccents = null);   // newest first, at most RecentLimit

// The user's choice, kept in the registry (HKCU\Software\Vde\Appearance) so Settings, the shell and
// any other program see the same thing, and changing it re-themes everything that is listening.
public static class ThemeSettings
{
    public const string KeyPath = @"Software\Vde\Appearance";
    public const int RecentLimit = 5;

    // The colour moved to the front of the recent list (no duplicates, capped).
    public static IReadOnlyList<Color> WithRecent(IReadOnlyList<Color>? recent, Color color)
        => (recent ?? []).Where(c => c != color).Prepend(color).Take(RecentLimit).ToList();

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

        bool Flag(string name, bool fallback) => RegistryBool.Get(hive, KeyPath, name, fallback);

        var recent = new List<Color>();
        if (hive.GetValue(KeyPath, "RecentAccents") is { Kind: RegistryValueKind.MultiString } r && r.Data is string[] list)
            foreach (var part in list)
                if (Color.TryParse(part, out var c) && !recent.Contains(c) && recent.Count < RecentLimit)
                    recent.Add(c);

        return new ThemeSelection(id, variant, accent, Flag("Transparency", true), Flag("AccentOnTaskbar", false), Flag("AccentOnTitleBars", false), recent.Count > 0 ? recent : null);
    }

    public static void Save(IHive hive, ThemeSelection selection)
    {
        hive.SetValue(KeyPath, "Theme", RegistryValueKind.String, selection.ThemeId);
        hive.SetValue(KeyPath, "Variant", RegistryValueKind.String, selection.Variant.ToString());
        if (selection.Accent is { } accent)
            hive.SetValue(KeyPath, "Accent", RegistryValueKind.String, accent.ToString());
        else
            hive.DeleteValue(KeyPath, "Accent");
        RegistryBool.Set(hive, KeyPath, "Transparency", selection.Transparency);
        RegistryBool.Set(hive, KeyPath, "AccentOnTaskbar", selection.AccentOnTaskbar);
        RegistryBool.Set(hive, KeyPath, "AccentOnTitleBars", selection.AccentOnTitleBars);
        hive.SetValue(KeyPath, "RecentAccents", RegistryValueKind.MultiString, (selection.RecentAccents ?? []).Take(RecentLimit).Select(c => c.ToString()).ToArray());
    }
}
