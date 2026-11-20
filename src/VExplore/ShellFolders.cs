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
    public const string Home = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7015}";
    public const string Desktop = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7016}";
    public const string Downloads = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7017}";
    public const string Music = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7018}";
    public const string Pictures = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B7019}";
    public const string Videos = "{7B2F6C1E-5D0A-4F3B-9E48-1C0D2A6B701A}";

    // The namespaces a folder lists others in. "Sidebar" is the file manager's places, "Desktop" the icons on the
    // desktop, "Computer" what This Computer shows first, and "Network" and "ControlPanel" what those folders add to
    // their own items. A user hides one with Software\Vde\Explorer\Namespaces\<place>\<clsid> Hidden=1.
    public static string[] Places(string clsid) => clsid switch
    {
        Computer or Network or Trash => ["Sidebar", "Desktop"],
        Home => ["Sidebar"],
        Documents or Desktop or Downloads or Music or Pictures or Videos => ["Sidebar", "Computer"],
        _ => [],
    };

    // clsid, parsing name ("shell:Computer"), display name, order (the sidebar's, and This Computer's)
    public static readonly (string Clsid, string Parsing, string Name, int Order)[] All =
    [
        (Home, "Home", "Home", 1),
        (Desktop, "Desktop", "Desktop", 2),
        (Documents, "Documents", "Documents", 3),
        (Downloads, "Downloads", "Downloads", 4),
        (Music, "Music", "Music", 5),
        (Pictures, "Pictures", "Pictures", 6),
        (Videos, "Videos", "Videos", 7),
        (Computer, "Computer", "This Computer", 10),
        (Network, "Network", "Network", 20),
        (Trash, "Trash", "Trash", 30),
        (ControlPanel, "ControlPanel", "Control Panel", 40),
    ];
}

[VdeComponent(ShellFolderIds.Computer, ProgId = "VExplore.Computer", Description = "This Computer", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class ComputerShellFolder : ComputerFolder
{
    public ComputerShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public ComputerShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Network, ProgId = "VExplore.Network", Description = "Network", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class NetworkShellFolder() : NetworkFolder();

[VdeComponent(ShellFolderIds.Trash, ProgId = "VExplore.Trash", Description = "Trash", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class TrashShellFolder() : TrashFolder();

// The user's own folders: shell folders that stand for their directories, listed in the sidebar and in This Computer.
[VdeComponent(ShellFolderIds.Home, ProgId = "VExplore.Home", Description = "Home", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class HomeShellFolder : HomeFolder
{
    public HomeShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public HomeShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Desktop, ProgId = "VExplore.Desktop", Description = "Desktop", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class DesktopShellFolder : DesktopFolder
{
    public DesktopShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public DesktopShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Documents, ProgId = "VExplore.Documents", Description = "Documents", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class DocumentsShellFolder : DocumentsFolder
{
    public DocumentsShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public DocumentsShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Downloads, ProgId = "VExplore.Downloads", Description = "Downloads", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class DownloadsShellFolder : DownloadsFolder
{
    public DownloadsShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public DownloadsShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Music, ProgId = "VExplore.Music", Description = "Music", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class MusicShellFolder : MusicFolder
{
    public MusicShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public MusicShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Pictures, ProgId = "VExplore.Pictures", Description = "Pictures", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class PicturesShellFolder : PicturesFolder
{
    public PicturesShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public PicturesShellFolder(KnownFolders? folders) : base(folders) { }
}

[VdeComponent(ShellFolderIds.Videos, ProgId = "VExplore.Videos", Description = "Videos", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class VideosShellFolder : VideosFolder
{
    public VideosShellFolder() : base() { }   // a registered component is created through its parameterless constructor
    public VideosShellFolder(KnownFolders? folders) : base(folders) { }
}

// The Control Panel folder: the shell sets Opener to launch the chosen page.
[VdeComponent(ShellFolderIds.ControlPanel, ProgId = "VExplore.ControlPanel", Description = "Control Panel", ThreadingModel = ThreadingModel.Apartment,
    Categories = [ComponentCategories.ShellFolder])]
public sealed class ControlPanelShellFolder() : ControlPanelFolder(LoadCatalog, item => Opener?.Invoke(item))
{
    public static Action<ControlPanelItem>? Opener { get; set; }

    private static ControlPanelCatalog LoadCatalog()
        => ControlPanelCatalog.Load(null, null, ShellData.Apps(Vde.Freedesktop.Locale.FromEnvironment()), ShellApp.Desktops());
}

public static class ShellFolderSetup
{
    public const ShellFolderAttributes Attributes = ShellFolderAttributes.Folder | ShellFolderAttributes.BrowseInPlace;

    // The shell's folders added to a catalog as built-ins: found by name, listed in the sidebar, no registration needed.
    public static void AddBuiltIns(ShellFolderCatalog catalog, KnownFolders? folders = null)
    {
        void Add(string clsid, IShellFolder folder)
        {
            var (_, parsing, name, order) = ShellFolderIds.All.First(f => f.Clsid == clsid);
            catalog.AddBuiltIn(clsid, parsing, name, Attributes, folder, ShellFolderIds.Places(clsid), order);
        }
        Add(ShellFolderIds.Home, new HomeShellFolder(folders));
        Add(ShellFolderIds.Desktop, new DesktopShellFolder(folders));
        Add(ShellFolderIds.Documents, new DocumentsShellFolder(folders));
        Add(ShellFolderIds.Downloads, new DownloadsShellFolder(folders));
        Add(ShellFolderIds.Music, new MusicShellFolder(folders));
        Add(ShellFolderIds.Pictures, new PicturesShellFolder(folders));
        Add(ShellFolderIds.Videos, new VideosShellFolder(folders));
        Add(ShellFolderIds.Computer, new ComputerShellFolder(folders));
        Add(ShellFolderIds.Network, new NetworkShellFolder());
        Add(ShellFolderIds.Trash, new TrashShellFolder());
        Add(ShellFolderIds.ControlPanel, new ControlPanelShellFolder());
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
            RegistryNumber.Set(classes, key, "Attributes", (int)Attributes);
            classes.SetValue(key, "Namespaces", RegistryValueKind.MultiString, ShellFolderIds.Places(clsid));
            RegistryNumber.Set(classes, key, "Order", order);
        }
    }
}
