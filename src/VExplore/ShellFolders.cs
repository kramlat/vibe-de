using Vde.Components;
using Vde.Shell;
using Vibe.Registry;

namespace VExplore;

// The virtual folders the shell itself provides, as shell folder components: the same thing an installer would
// register for its own namespace extension. They are apartment-threaded (used from the UI thread only), shown in
// the sidebar, and built into the shell too, so they work with no registration.
public static class ShellFolderIds
{
    public const string Computer = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7010}";
    public const string Network = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7011}";
    public const string Trash = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7012}";
    public const string Documents = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7013}";
    public const string ControlPanel = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7014}";

    // Where each folder appears: the sidebar of the file manager, and for This Computer, Network and Trash the
    // desktop as well (icons, as on the desktops people are used to). A user hides one with
    // Software\Vde\Explorer\Namespaces\<place>\<clsid> Hidden=1.
    public static string[] Places(string clsid) => clsid switch
    {
        Computer => ["Sidebar", "Desktop"],
        Network or Trash => ["Sidebar", "Desktop"],
        ControlPanel => [],
        _ => ["Sidebar"],
    };

    // clsid, parsing name ("shell:Computer"), display name, sidebar order
    public static readonly (string Clsid, string Parsing, string Name, int Order)[] All =
    [
        (Documents, "Documents", "Documents", 5),
        (Computer, "Computer", "This Computer", 10),
        (Network, "Network", "Network", 20),
        (Trash, "Trash", "Trash", 30),
        (ControlPanel, "ControlPanel", "Control Panel", 40),
    ];
}

[VdeComponent(ShellFolderIds.Computer, ProgId = "VExplore.Computer", Description = "This Computer", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class ComputerShellFolder(KnownFolders? folders = null) : ComputerFolder(folders);

[VdeComponent(ShellFolderIds.Network, ProgId = "VExplore.Network", Description = "Network", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class NetworkShellFolder() : NetworkFolder();

[VdeComponent(ShellFolderIds.Trash, ProgId = "VExplore.Trash", Description = "Trash", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class TrashShellFolder() : TrashFolder();

[VdeComponent(ShellFolderIds.Documents, ProgId = "VExplore.Documents", Description = "Documents", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class DocumentsShellFolder(KnownFolders? folders = null) : DocumentsFolder(folders);

// The Control Panel folder: the shell sets Opener to launch the chosen page.
[VdeComponent(ShellFolderIds.ControlPanel, ProgId = "VExplore.ControlPanel", Description = "Control Panel", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class ControlPanelShellFolder() : ControlPanelFolder(LoadCatalog, item => Opener?.Invoke(item))
{
    public static Action<ControlPanelItem>? Opener { get; set; }

    private static ControlPanelCatalog LoadCatalog()
        => ControlPanelCatalog.Load(null, null, Vde.Freedesktop.DesktopEntryDatabase.Load(Vde.Freedesktop.XdgDirectories.Default, Vde.Freedesktop.Locale.FromEnvironment()), ShellApp.Desktops());
}

public static class ShellFolderSetup
{
    public const ShellFolderAttributes Attributes = ShellFolderAttributes.Folder | ShellFolderAttributes.BrowseInPlace;

    // The shell's folders added to a catalog as built-ins: found by name, listed in the sidebar, no registration needed.
    public static void AddBuiltIns(ShellFolderCatalog catalog, KnownFolders? folders = null)
    {
        catalog.AddBuiltIn(ShellFolderIds.Computer, "Computer", "This Computer", Attributes, new ComputerShellFolder(folders), ShellFolderIds.Places(ShellFolderIds.Computer), 10);
        catalog.AddBuiltIn(ShellFolderIds.Network, "Network", "Network", Attributes, new NetworkShellFolder(), ShellFolderIds.Places(ShellFolderIds.Network), 20);
        catalog.AddBuiltIn(ShellFolderIds.Trash, "Trash", "Trash", Attributes, new TrashShellFolder(), ShellFolderIds.Places(ShellFolderIds.Trash), 30);
        catalog.AddBuiltIn(ShellFolderIds.Documents, "Documents", "Documents", Attributes, new DocumentsShellFolder(folders), ["Sidebar"], 5);
        catalog.AddBuiltIn(ShellFolderIds.ControlPanel, "ControlPanel", "Control Panel", Attributes, new ControlPanelShellFolder(), [], 40);
    }

    // What the installer writes next to RegSvrVDE's own registration: CLSID\{guid}\ShellFolder.
    public static void Register(ClassesRoot classes, bool installing)
    {
        foreach (var (clsid, parsing, name, order) in ShellFolderIds.All)
        {
            var key = $@"CLSID\{clsid}\ShellFolder";
            if (!installing) continue;   // unregistering removes the whole CLSID key, ShellFolder included
            classes.SetValue(key, "ParsingName", RegistryValueKind.String, parsing);
            classes.SetValue(key, "Name", RegistryValueKind.String, name);
            classes.SetValue(key, "Attributes", RegistryValueKind.DWord, (uint)Attributes);
            classes.SetValue(key, "Namespaces", RegistryValueKind.MultiString, ShellFolderIds.Places(clsid));
            classes.SetValue(key, "Order", RegistryValueKind.DWord, (uint)order);
        }
    }
}
