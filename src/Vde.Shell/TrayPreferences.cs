using Vibe.Registry;

namespace Vde.Shell;

public sealed record TrayEntry(string Key, string Title, string IconName, bool InCorner);

// Personalization > Taskbar > Taskbar corner overflow: which tray icons stay in the taskbar's corner; the others wait in the
// overflow flyout behind the chevron. The taskbar remembers every icon it has seen (HKCU\Software\Vde\TaskbarTray\<key>: Title,
// Icon) so the settings window, a different process, can list them; InCorner is the user's choice (absent: in the corner).
public static class TrayPreferences
{
    public const string KeyPath = @"Software\Vde\TaskbarTray";

    // A registry-safe name for an item (its bus name or application id).
    public static string KeyFor(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "item" : new string(chars);
    }

    public static bool InCorner(IHive hive, string key) => RegistryBool.Get(hive, $@"{KeyPath}\{key}", "InCorner") ?? true;

    public static void Set(IHive hive, string key, bool inCorner) => RegistryBool.Set(hive, $@"{KeyPath}\{key}", "InCorner", inCorner);

    // Notes an icon the taskbar is showing; writes only when something changed.
    public static void Remember(IHive hive, string key, string title, string iconName)
    {
        var path = $@"{KeyPath}\{key}";
        if (hive.GetValue(path, "Title") is not { Kind: RegistryValueKind.String } t || (string?)t.Data != title)
            hive.SetValue(path, "Title", RegistryValueKind.String, title);
        if (hive.GetValue(path, "Icon") is not { Kind: RegistryValueKind.String } i || (string?)i.Data != iconName)
            hive.SetValue(path, "Icon", RegistryValueKind.String, iconName);
    }

    public static IReadOnlyList<TrayEntry> Known(IHive hive)
    {
        if (!hive.KeyExists(KeyPath)) return [];
        string Read(string key, string name) => hive.GetValue($@"{KeyPath}\{key}", name) is { Kind: RegistryValueKind.String } v ? (string?)v.Data ?? "" : "";
        return hive.GetSubKeyNames(KeyPath)
            .Select(k => new TrayEntry(k, Read(k, "Title") is { Length: > 0 } title ? title : k, Read(k, "Icon"), InCorner(hive, k)))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The known icons as switches for the settings page.
    public static IReadOnlyList<SettingDefinition> Definitions(IHive hive) => Known(hive)
        .Select(e => new SettingDefinition("tray-" + e.Key, e.Title, SettingKind.Toggle, $@"{KeyPath}\{e.Key}", "InCorner") { Default = true, IconName = e.IconName })
        .ToList();
}
