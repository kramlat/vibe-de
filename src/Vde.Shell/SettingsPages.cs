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
    public long Step { get; init; } = 100;                              // what the up/down arrows add
    public long Max { get; init; } = long.MaxValue;
    public object? Default { get; init; }                               // what it shows when the registry has nothing
}

public sealed record SettingsPage(string Id, string Title, IReadOnlyList<SettingDefinition> Settings);

public static class BuiltInPages
{
    public static readonly SettingsPage Notifications = new("notifications", "Notifications",
    [
        new("dnd", "Do not disturb", SettingKind.Toggle, @"Software\Vde\Notifications", "DoNotDisturb", "Only critical notifications are shown.") { Default = false },
        new("timeout", "Show notifications for (milliseconds)", SettingKind.Number, @"Software\Vde\Notifications", "DefaultTimeoutMs") { Min = 500, Max = 600_000, Default = 5000L },
        new("history", "Notifications kept in history", SettingKind.Number, @"Software\Vde\Notifications", "HistoryLimit") { Min = 0, Max = 10_000, Default = 200L },
    ]);

    public static readonly SettingsPage Keyboard = new("keyboard", "Keyboard",
    [
        new("shortcuts", "Keyboard shortcuts", SettingKind.Choice, ShortcutStyleService.KeyPath, "ShortcutStyle", "Automatic uses Command (⌘) shortcuts only while an Apple keyboard is attached.")
        {
            Default = "Auto",
            Choices = [new("Auto", "Automatic (Apple shortcuts only with an Apple keyboard)"), new("Pc", "PC shortcuts (Ctrl)"), new("Mac", "Apple shortcuts (⌘)")],
        },
    ]);

    public static readonly SettingsPage StartMenu = new("startmenu", "Start menu",
    [
        new("layout", "Layout", SettingKind.Choice, LauncherModel.KeyPath, "Layout", "Applies the next time the Start menu opens.")
        {
            Default = "Modern",
            Choices = [new("Modern", "Windows 11 style: pinned grid and recommended"), new("Classic", "Windows 10 style: app list with pinned tiles")],
        },
    ]);

    public const string TerminalKey = @"Software\Vde\Terminal";

    public static readonly SettingsPage Terminal = new("terminal", "Terminal",
    [
        new("profile", "Default profile", SettingKind.Text, TerminalKey, "DefaultProfile", "The name of a shell below, e.g. PowerShell or bash. Empty: your login shell.") { Default = "" },
        new("font", "Font", SettingKind.Text, TerminalKey, "FontFamily", "A monospace font installed on this computer.") { Default = "monospace" },
        new("size", "Font size", SettingKind.Number, TerminalKey, "FontSize") { Min = 6, Max = 72, Default = 13L },
        new("copy", "Copy text as soon as it is selected", SettingKind.Toggle, TerminalKey, "CopyOnSelect") { Default = false },
    ]);

    public const string PowerKey = @"Software\Vde\Power";

    // Settings only for now: nothing yet turns the screen off, sleeps or locks from them.
    public static readonly SettingsPage Power = new("power", "Screen timeout",
    [
        new("screenoff", "Turn off my screen after (minutes, 0 is never)", SettingKind.Number, PowerKey, "ScreenOffMinutes") { Min = 0, Max = 1440, Step = 1, Default = 10L },
        new("sleep", "Make my device sleep after (minutes, 0 is never)", SettingKind.Number, PowerKey, "SleepMinutes") { Min = 0, Max = 1440, Step = 1, Default = 30L },
        new("lock", "Lock the screen when it turns off", SettingKind.Toggle, PowerKey, "LockOnScreenOff") { Default = true },
    ]);

    public const string ScreenSaverKey = @"Software\Vde\ScreenSaver";

    // Settings only for now: no screen saver program runs yet.
    public static readonly SettingsPage ScreenSaver = new("screensaver", "Screen saver",
    [
        new("saver", "Screen saver", SettingKind.Choice, ScreenSaverKey, "Name")
        {
            Default = "None", Choices = [new("None", "(None)"), new("Blank", "Blank"), new("Clock", "Clock")],
        },
        new("wait", "Wait (minutes)", SettingKind.Number, ScreenSaverKey, "WaitMinutes") { Min = 1, Max = 1440, Step = 1, Default = 5L },
        new("logon", "On resume, display the lock screen", SettingKind.Toggle, ScreenSaverKey, "LockOnResume") { Default = false },
    ]);

    public const string WindowsKey = @"Software\Vde\Windows";

    public static readonly SettingsPage Windows = new("windows", "Windows",
    [
        new("doubleclick", "Double-clicking a titlebar", SettingKind.Choice, WindowsKey, "TitlebarDoubleClick", "Applies to windows whose titlebar vwm draws.")
        {
            Default = "Maximize",
            Choices = [new("Maximize", "Maximizes or restores the window"), new("Minimize", "Minimizes the window"), new("Close", "Closes the window"), new("None", "Does nothing")],
        },
    ]);

    public static readonly SettingsPage Taskbar = new("taskbar", "Taskbar",
    [
        new("edge", "Taskbar location", SettingKind.Choice, TaskbarModel.KeyPath, "Edge")
        {
            Default = "Bottom",
            Choices = [new("Bottom", "Bottom"), new("Top", "Top"), new("Left", "Left"), new("Right", "Right")],
        },
        new("alignment", "Start button and apps", SettingKind.Choice, TaskbarModel.KeyPath, "Alignment")
        {
            Default = "Center",
            Choices = [new("Center", "Centered"), new("Start", "Aligned to the start")],
        },
    ]);

    public static readonly IReadOnlyList<SettingsPage> Pages = [Power, ScreenSaver, StartMenu, Taskbar, Windows, Terminal, Notifications, Keyboard];

    public static readonly IReadOnlyList<ControlPanelItem> Items =
    [
        new("themes", "Themes", "Choose the style of your windows and controls", "preferences-desktop-theme", ControlPanelCategories.Appearance,
            ["theme", "themes", "personalization", "look", "style"], ItemSource.BuiltIn) { PageId = "themes" },
        new("colors", "Colors", "Accent color, light or dark mode, transparency effects", "preferences-desktop-color", ControlPanelCategories.Appearance,
            ["colour", "color", "accent", "dark", "light", "mode", "transparency", "mica", "title bar", "taskbar"], ItemSource.BuiltIn) { PageId = "colors" },
        new("lockscreen", "Lock screen", "Lock screen images, apps and animations", "system-lock-screen", ControlPanelCategories.Appearance,
            ["lock", "lockscreen", "sign-in", "picture", "slideshow", "background"], ItemSource.BuiltIn) { PageId = "lockscreen" },
        new("screensaver", "Screen saver", "Choose a screen saver and how long to wait for it", "preferences-desktop-screensaver", ControlPanelCategories.Appearance,
            ["screensaver", "screen saver", "idle", "blank"], ItemSource.BuiltIn) { PageId = "screensaver" },
        new("screentimeout", "Screen timeout", "When the screen turns off, the device sleeps and the screen locks", "preferences-system-power", ControlPanelCategories.System,
            ["power", "sleep", "screen off", "timeout", "battery", "idle"], ItemSource.BuiltIn) { PageId = "power" },
        new("desktop", "Desktop", "Wallpaper, background colour and desktop icons", "preferences-desktop-wallpaper", ControlPanelCategories.Appearance,
            ["wallpaper", "background", "picture", "image", "desktop", "icons", "colour", "color"], ItemSource.BuiltIn) { PageId = "desktop" },
        new("startmenu", "Start menu", "Windows 11 or Windows 10 layout", "start-here-vde", ControlPanelCategories.Appearance,
            ["start", "launcher", "layout", "tiles", "menu"], ItemSource.BuiltIn) { PageId = "startmenu" },
        new("windows", "Windows", "What double-clicking a titlebar does", "preferences-system-windows", ControlPanelCategories.Appearance,
            ["window", "titlebar", "double click", "maximize", "minimize"], ItemSource.BuiltIn) { PageId = "windows" },
        new("taskbar", "Taskbar", "Which screen edge it sits on and how apps are aligned", "preferences-desktop", ControlPanelCategories.Appearance,
            ["taskbar", "dock", "panel", "edge", "left", "right", "top", "bottom", "center", "centre"], ItemSource.BuiltIn) { PageId = "taskbar" },
        new("display", "Display", "Scale, resolution, orientation and multiple displays", "video-display", ControlPanelCategories.System,
            ["display", "screen", "monitor", "resolution", "scale", "brightness", "night light", "orientation", "arrange", "multiple"], ItemSource.BuiltIn) { PageId = "display" },
        new("terminal", "Terminal", "Default shell, font and copy behaviour of vterminal", "utilities-terminal", ControlPanelCategories.Apps,
            ["terminal", "shell", "powershell", "bash", "console", "font"], ItemSource.BuiltIn) { PageId = "terminal" },
        new("keyboard", "Keyboard", "Shortcut style: PC or Apple", "input-keyboard", ControlPanelCategories.Devices,
            ["shortcuts", "apple", "mac", "command", "ctrl", "hotkeys"], ItemSource.BuiltIn) { PageId = "keyboard" },
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
            SettingKind.Toggle => RegistryBool.Get(hive, definition.KeyPath, definition.ValueName) is { } on ? on : definition.Default,
            SettingKind.Number => RegistryNumber.Get(hive, definition.KeyPath, definition.ValueName) is { } n ? Math.Clamp(n, definition.Min, definition.Max) : definition.Default,
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
                RegistryBool.Set(hive, definition.KeyPath, definition.ValueName, on);
                return true;
            case SettingKind.Number when newValue is long n && n >= definition.Min && n <= definition.Max && n <= uint.MaxValue:
                RegistryNumber.Set(hive, definition.KeyPath, definition.ValueName, n);
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
