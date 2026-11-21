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
    public string? IconName { get; init; }                              // a small picture before the label (the tray icons)
    public bool AsCheckBox { get; init; }                               // a Toggle drawn as a check box, label beside it
}

public sealed record SettingsPage(string Id, string Title, IReadOnlyList<SettingDefinition> Settings)
{
    // Windows 11 style: the settings in collapsible sections, each with a title and a line saying what it holds.
    // Settings is then all of the sections' settings together.
    public IReadOnlyList<SettingsSection>? Sections { get; init; }

    // Shown before the title in the header ("Personalization › Taskbar"), for pages that sit under a heading.
    public string? Parent { get; init; }

    public static SettingsPage WithSections(string id, string title, params SettingsSection[] sections)
        => new(id, title, sections.SelectMany(s => s.Settings).ToList()) { Sections = sections };
}

public sealed record SettingsSection(string Title, string Help, IReadOnlyList<SettingDefinition> Settings, bool Expanded = true)
{
    // Settings that are not known until the page opens (the tray icons the taskbar has seen), read from the registry and
    // re-read when it changes. When set, Settings is empty.
    public Func<IHive, IReadOnlyList<SettingDefinition>>? Dynamic { get; init; }
    public string? DynamicKey { get; init; }   // the registry key it comes from: when it changes the section is built again
}

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

    // Both apply at once: the taskbar watches its key and docks and lays itself out again. Other sections (items,
    // corner icons, overflow) join as the taskbar grows the things they switch.
    public static readonly SettingsPage Taskbar = SettingsPage.WithSections("taskbar", "Taskbar",
        new SettingsSection("Taskbar items", "Show or hide buttons that appear on the taskbar",
        [
            new("search", "Search", SettingKind.Toggle, TaskbarModel.KeyPath, "ShowSearch", "Opens Start, ready to search") { Default = true },
            new("widgets", "Widgets", SettingKind.Toggle, TaskbarModel.KeyPath, "ShowWidgets", "VDE's widgets panel: weather, photos, system and headlines, and widgets the community makes") { Default = true },
            new("taskview", "Task view", SettingKind.Toggle, TaskbarModel.KeyPath, "ShowTaskView", "Shows all open windows to pick from") { Default = true },
        ]),
        new SettingsSection("Taskbar corner icons", "Show or hide icons that appear on the corner of your taskbar",
        [
            new("touchkeyboard", "Touch keyboard", SettingKind.Toggle, TaskbarModel.KeyPath, "ShowTouchKeyboard", "Always show the on-screen keyboard icon") { Default = false },
        ]),
        new SettingsSection("Taskbar corner overflow", "Choose which icons may appear in the taskbar corner \u2013 all others will appear in the taskbar corner overflow menu", [])
        {
            Dynamic = TrayPreferences.Definitions, DynamicKey = TrayPreferences.KeyPath,
        },
        new SettingsSection("Taskbar behaviors", "Where the taskbar sits, how it is aligned, whether it hides, and the show desktop corner",
        [
            new("edge", "Taskbar location on screen", SettingKind.Choice, TaskbarModel.KeyPath, "Edge")
            {
                Default = "Bottom",
                Choices = [new("Bottom", "Bottom"), new("Top", "Top"), new("Left", "Left"), new("Right", "Right")],
            },
            new("alignment", "Start menu position", SettingKind.Choice, TaskbarModel.KeyPath, "Alignment", "The Start button and the apps next to it")
            {
                Default = "Center",
                Choices = [new("Center", "Centered"), new("Start", "Aligned to the start")],
            },
            new("autohide", "Automatically hide the taskbar", SettingKind.Toggle, TaskbarModel.KeyPath, "AutoHide") { Default = false, AsCheckBox = true },
            new("showdesktop", "Select the far corner of the taskbar to show the desktop", SettingKind.Toggle, TaskbarModel.KeyPath, "ShowDesktopCorner") { Default = true, AsCheckBox = true },
        ])) with { Parent = "Personalization" };

    // VDE's widgets: the built-in cards, the weather's city and units, the headline feeds, and the community's widgets (each runs only when allowed).
    public static readonly SettingsPage Widgets = SettingsPage.WithSections("widgets", "Widgets",
        new SettingsSection("Widgets", "Show or hide the cards in the widgets panel",
            WidgetCatalog.All.Select(w => new SettingDefinition(w.Id.ToLowerInvariant(), w.Title, SettingKind.Toggle, WidgetSettings.ShowKey, w.Id) { Default = true, IconName = w.IconNames[0] }).ToList()),
        new SettingsSection("Weather", "The weather comes from Open-Meteo, open data that needs no account",
        [
            new("city", "City", SettingKind.Text, WidgetSettings.KeyPath, "WeatherCity", "For example Berlin, or Springfield. Only this name is sent to the weather service.") { Default = "" },
            new("units", "Temperature", SettingKind.Choice, WidgetSettings.KeyPath, "WeatherUnits")
            {
                Default = "Auto", Choices = [new("Auto", "Follow my region"), new("Celsius", "Celsius"), new("Fahrenheit", "Fahrenheit")],
            },
        ]),
        new SettingsSection("Top stories", "Headlines from the feeds you choose",
        [
            new("feeds", "News feeds", SettingKind.Text, WidgetSettings.KeyPath, "Feeds", "The addresses of RSS or Atom feeds, separated by spaces.") { Default = "" },
        ]),
        new SettingsSection("Community widgets", "Widgets other people made, from apps or components. One runs only while it is allowed here.", [])
        {
            Dynamic = WidgetProviders.Definitions, DynamicKey = WidgetProviders.KeyPath,
        });

    // File Explorer's options (the "..." menu > Options).
    public static readonly SettingsPage FileExplorer = new("fileexplorer", "File Explorer options",
    [
        new("opento", "Open File Explorer to", SettingKind.Choice, ExplorerOptions.KeyPath, "OpenTo", "Where a new File Explorer window starts when no folder was asked for.")
        {
            Default = "Home", Choices = [new("Home", "Home"), new("QuickAccess", "Quick access"), new("Computer", "This Computer")],
        },
    ]);

    public static readonly SettingsPage StartFolderPage = StartFolders.Page();

    public static readonly IReadOnlyList<SettingsPage> Pages = [Power, ScreenSaver, StartMenu, StartFolderPage, Widgets, FileExplorer, Taskbar, Windows, Terminal, Notifications, Keyboard];

    // Windows settings pages that VDE will never have, on purpose: they exist to serve Microsoft's own services and advertising.
    //   - Personalization > Device usage (picks what personalized tips, ads and recommendations "within Microsoft experiences" are shown)
    // Copy the layout of a Windows page, never a page whose whole point is a Microsoft service. See the "skip MS-only items" rule.
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
        new("startfolders", "Start folders", "Choose which folders appear on Start next to the Power button", "start-here-vde", ControlPanelCategories.Appearance,
            ["start", "folders", "power button", "documents", "downloads", "settings", "file explorer", "network", "home"], ItemSource.BuiltIn) { PageId = "startfolders" },
        new("widgets", "Widgets", "Weather, photos, headlines and widgets the community makes", "preferences-desktop-widgets", ControlPanelCategories.Appearance,
            ["widget", "widgets", "gadget", "gadgets", "weather", "news", "feed", "rss", "panel"], ItemSource.BuiltIn) { PageId = "widgets" },
        new("fileexplorer", "File Explorer options", "Where File Explorer opens", "system-file-manager", ControlPanelCategories.Appearance,
            ["explorer", "file explorer", "folder options", "options", "files", "open to", "home", "quick access"], ItemSource.BuiltIn) { PageId = "fileexplorer" },
        new("fonts", "Fonts", "Install fonts and see the ones you have", "preferences-desktop-font", ControlPanelCategories.Appearance,
            ["font", "fonts", "typeface", "install", "ttf", "otf", "text"], ItemSource.BuiltIn) { PageId = "fonts" },
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
