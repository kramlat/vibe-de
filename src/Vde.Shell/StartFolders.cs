using Vibe.Registry;

namespace Vde.Shell;

// One of the places that can sit on the Start menu next to the Power button.
public sealed record StartFolder(string Id, string Label, IReadOnlyList<string> IconNames, bool DefaultOn);

// Personalization > Start > Folders: which of these Start shows, kept as one value each under
// HKCU\Software\Vde\Start\Folders. Settings and File Explorer are actions, the rest are shell folders
// ("shell:Documents"), so they open through the VFS like any other place.
public static class StartFolders
{
    public const string KeyPath = @"Software\Vde\Start\Folders";

    // In the order Windows lays them out.
    public static readonly IReadOnlyList<StartFolder> All =
    [
        new("Settings", "Settings", ["preferences-system", "configure"], true),
        new("Files", "File Explorer", ["system-file-manager", "folder"], true),
        new("Documents", "Documents", ["folder-documents", "folder"], true),
        new("Downloads", "Downloads", ["folder-download", "folder"], true),
        new("Music", "Music", ["folder-music", "folder"], false),
        new("Pictures", "Pictures", ["folder-pictures", "folder"], false),
        new("Videos", "Videos", ["folder-videos", "folder"], false),
        new("Network", "Network", ["network-workgroup", "folder-remote"], false),
        new("Home", "Home folder", ["user-home", "folder"], false),
    ];

    // The shell location a folder entry opens (null for the Settings action and File Explorer, which have their own).
    public static string? Location(string id) => id switch
    {
        "Documents" => "shell:Documents", "Downloads" => "shell:Downloads", "Music" => "shell:Music", "Pictures" => "shell:Pictures",
        "Videos" => "shell:Videos", "Network" => "shell:Network", "Home" => "shell:Home", _ => null,
    };

    public static bool IsOn(IHive hive, StartFolder folder) => RegistryBool.Get(hive, KeyPath, folder.Id) ?? folder.DefaultOn;

    public static IReadOnlyList<StartFolder> Visible(IHive hive) => All.Where(f => IsOn(hive, f)).ToList();

    public static void Set(IHive hive, string id, bool on) => RegistryBool.Set(hive, KeyPath, id, on);

    // The same list as a settings page, for the Control Panel.
    public static SettingsPage Page() => new("startfolders", "Start folders",
        All.Select(f => new SettingDefinition(f.Id.ToLowerInvariant(), f.Label, SettingKind.Toggle, KeyPath, f.Id) { Default = f.DefaultOn }).ToList());
}

// File Explorer's options, kept in HKCU\Software\Vde\Explorer.
public static class ExplorerOptions
{
    public const string KeyPath = @"Software\Vde\Explorer";

    // Where a new window starts: "Home" (the default), "QuickAccess" or "Computer".
    public static string OpenTo(IHive hive)
        => hive.GetValue(KeyPath, "OpenTo") is { Kind: RegistryValueKind.String, Data: string s } && s is "Home" or "QuickAccess" or "Computer" ? s : "Home";
}
