using Vibe.Registry;

namespace Vde.Shell;

public enum SettingKind { Toggle, Choice, Number, Text, Color }

public sealed record SettingChoice(string Value, string Label);

// One editable setting, described as data: where it lives in the registry and what values are allowed.
// A window renders it (a switch, a drop-down, a slider...) without knowing what it is for.
public sealed record SettingDefinition(
    string Id, string Label, SettingKind Kind, string KeyPath, string ValueName, string? Help = null)
{
    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];   // Choice
    public long Min { get; init; }                                      // Number
    public long Max { get; init; } = long.MaxValue;
    public object? Default { get; init; }                               // what it shows when the registry has nothing
}

public sealed record SettingsPage(string Id, string Title, IReadOnlyList<SettingDefinition> Settings);

public static class BuiltInPages
{
    public static readonly SettingsPage Appearance = new("appearance", "Appearance",
    [
        new("theme", "Theme", SettingKind.Choice, @"Software\Vde\Appearance", "Theme") { Default = "fluent" },   // choices come from the theme catalog
        new("variant", "Mode", SettingKind.Choice, @"Software\Vde\Appearance", "Variant")
        {
            Default = "System",
            Choices = [new("System", "Use the system setting"), new("Light", "Light"), new("Dark", "Dark")],
        },
        new("accent", "Accent colour", SettingKind.Color, @"Software\Vde\Appearance", "Accent", "Leave empty for the theme's own accent."),
    ]);

    public static readonly SettingsPage Notifications = new("notifications", "Notifications",
    [
        new("dnd", "Do not disturb", SettingKind.Toggle, @"Software\Vde\Notifications", "DoNotDisturb", "Only critical notifications are shown.") { Default = false },
        new("timeout", "Show notifications for (milliseconds)", SettingKind.Number, @"Software\Vde\Notifications", "DefaultTimeoutMs") { Min = 500, Max = 600_000, Default = 5000L },
        new("history", "Notifications kept in history", SettingKind.Number, @"Software\Vde\Notifications", "HistoryLimit") { Min = 0, Max = 10_000, Default = 200L },
    ]);

    public static readonly IReadOnlyList<SettingsPage> Pages = [Appearance, Notifications];

    public static readonly IReadOnlyList<ControlPanelItem> Items =
    [
        new("appearance", "Appearance", "Theme, light or dark mode and accent colour", "preferences-desktop-theme", ControlPanelCategories.Appearance,
            ["theme", "dark", "light", "colour", "color", "accent", "look"], ItemSource.BuiltIn) { PageId = "appearance" },
        new("notifications", "Notifications", "Do not disturb and how long notifications stay", "preferences-desktop-notification", ControlPanelCategories.System,
            ["dnd", "focus", "quiet", "toast", "alerts"], ItemSource.BuiltIn) { PageId = "notifications" },
    ];

    public static SettingsPage? Find(string id) => Pages.FirstOrDefault(p => p.Id == id);
}

// Reads and writes one setting. Values are checked against the definition on the way in, so a page can never
// store something the rest of the desktop would have to defend against.
public sealed class SettingBinding(IHive hive, SettingDefinition definition)
{
    public SettingDefinition Definition => definition;

    public object? Read()
    {
        var value = hive.GetValue(definition.KeyPath, definition.ValueName);
        if (value is null)
            return definition.Default;
        return definition.Kind switch
        {
            SettingKind.Toggle => value switch { { Kind: RegistryValueKind.DWord, Data: uint d } => d != 0, { Kind: RegistryValueKind.Bool, Data: bool b } => b, _ => definition.Default },
            SettingKind.Number => value switch { { Kind: RegistryValueKind.DWord, Data: uint d } => Math.Clamp(d, definition.Min, definition.Max), _ => definition.Default },
            SettingKind.Choice or SettingKind.Text or SettingKind.Color => value is { Kind: RegistryValueKind.String, Data: string s } ? s : definition.Default,
            _ => definition.Default,
        };
    }

    // Returns false (and changes nothing) when the value is not allowed for this setting.
    public bool Write(object? newValue)
    {
        switch (definition.Kind)
        {
            case SettingKind.Toggle when newValue is bool on:
                hive.SetValue(definition.KeyPath, definition.ValueName, RegistryValueKind.DWord, on ? 1u : 0u);
                return true;
            case SettingKind.Number when newValue is long n && n >= definition.Min && n <= definition.Max && n <= uint.MaxValue:
                hive.SetValue(definition.KeyPath, definition.ValueName, RegistryValueKind.DWord, (uint)n);
                return true;
            case SettingKind.Choice when newValue is string c && (definition.Choices.Count == 0 || definition.Choices.Any(x => x.Value == c)) && c.Length > 0:
            case SettingKind.Text when newValue is string:
                hive.SetValue(definition.KeyPath, definition.ValueName, RegistryValueKind.String, newValue);
                return true;
            case SettingKind.Color when newValue is string color && System.Text.RegularExpressions.Regex.IsMatch(color, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"):
                hive.SetValue(definition.KeyPath, definition.ValueName, RegistryValueKind.String, color);
                return true;
            case SettingKind.Color when newValue is "" or null:   // clearing the accent
                hive.DeleteValue(definition.KeyPath, definition.ValueName);
                return true;
            default:
                return false;
        }
    }
}
