using Vibe.Registry;

namespace Vde.Components;

// How calls into a component are scheduled, as in COM. The numbers are part of the contract: RegSvrVDE reads
// them out of the attribute without loading the assembly.
public enum ThreadingModel
{
    Apartment = 0,   // the component lives on one thread (the UI thread); callers from elsewhere are marshalled to it
    Free = 1,        // callable from any thread; the component does its own locking
    Both = 2,        // works either way
}

// Declares a class as a component RegSvrVDE can register. The CLSID is a GUID, "{...}".
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VdeComponentAttribute(string clsid) : Attribute
{
    public string Clsid { get; } = clsid;
    public string? ProgId { get; set; }                  // "Vde.Explorer.SettingsPortal"
    public string? Description { get; set; }
    public ThreadingModel ThreadingModel { get; set; } = ThreadingModel.Apartment;
    public string[]? Categories { get; set; }            // GUIDs of the categories the component belongs to
}

// Names the class that handles RegSvrVDE's /i switch (extra work at install or uninstall).
// This is the only code of a component that RegSvrVDE ever runs, and only when asked to with /i.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class VdeInstallerAttribute(Type installerType) : Attribute
{
    public Type InstallerType { get; } = installerType;
}

public interface IVdeInstaller
{
    void Install(VdeInstallContext context);
}

// Registry is HKCR of the hive being registered into.
public sealed record VdeInstallContext(ClassesRoot Registry, bool Installing, string? CommandLine, string AssemblyPath);

// A component in this category adds results to the launcher's search. The user must authorize each one (see
// the launcher's provider list); an unauthorized provider is never loaded or asked anything.
public static class ComponentCategories
{
    public const string LauncherSearchProvider = "{6C0E5A7B-4D0A-4F63-9B2E-5A5C1F6D0001}";
    public const string ShellFolder = "{6C0E5A7B-4D0A-4F63-9B2E-5A5C1F6D0002}";
    public const string Widget = "{6C0E5A7B-4D0A-4F63-9B2E-5A5C1F6D0003}";
}

public sealed record SearchHit(string Title, string Subtitle = "", string Icon = "", string? ActivationId = null);

// What a launcher search plugin implements. Implementations must be quick and must not assume they are
// called on a particular thread; the launcher cancels slow ones and ignores their results.
public interface ILauncherSearchProvider
{
    string Name { get; }
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken cancel);

    // The user picked one of this provider's hits.
    Task ActivateAsync(SearchHit hit, CancellationToken cancel);
}

// ---- widgets: what the community adds to the widgets panel ----

// A widget is a small live card in the widgets panel. Register a class that implements this in ComponentCategories.Widget
// (apartment-threaded: it lives on the UI thread). The user must allow each widget (Settings > Widgets) and the registration
// must be trusted, as with launcher search providers; nothing is loaded otherwise. A widget is the community's own small program
// of the kind the Windows 7 gadgets were: it draws its card and keeps it up to date. It is not a place for advertising: the panel
// shows only what the widget draws, and a widget that does not behave is simply not allowed.
public interface IWidgetProvider
{
    string Title { get; }
    IReadOnlyList<string> IconNames { get; }   // freedesktop icon names, best first

    // The card's content: an Avalonia Control (typed as object so this assembly needs no UI dependency), made on the UI thread.
    object CreateContent();
}

// ---- property sheets: what a component implements to add a tab to a file's Properties window ----

// What the property sheet is about: the files and folders it was opened for.
public sealed record PropertyTarget(IReadOnlyList<string> Paths, string MimeType)
{
    public bool IsSingle => Paths.Count == 1;
}

// One extra tab. CreateContent returns an Avalonia Control (typed as object so this assembly needs no UI
// dependency); the sheet calls it on the UI thread when the tab is first shown. Apply is called when the user
// presses OK or Apply, on the UI thread, and returns false to keep the sheet open (after explaining why).
// IsDirty says whether Apply has anything to do (without it, a page with an Apply is always applied). Tag is the
// provider's own object for the page, which a host or test can get back.
public sealed record PropertyPageInfo(string Title, Func<object> CreateContent, Func<bool>? Apply = null, int Order = 100, Func<bool>? IsDirty = null, object? Tag = null);

// Implemented by components registered under  <class>\shellex\PropertySheetHandlers\<name>  (default value =
// the component's CLSID), where <class> is "*" (every file), "Directory", an extension such as ".png", or a ProgID.
public interface IPropertyPageProvider
{
    IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target);
}

// ---- shell folders: virtual folders (This Computer, Network, Trash, ...) that a component presents to the file manager ----

[Flags]
public enum ShellFolderAttributes : uint
{
    None = 0,
    Folder = 1,          // behaves as a folder: the file manager can navigate into it
    BrowseInPlace = 2,   // opens in the same window rather than a new one
    HasSubFolder = 4,    // shows an expander in a folder tree
    Hidden = 8,          // exists, but is not listed in the sidebar or on the desktop unless asked for by name
}

// One entry in a shell folder. It may stand for something real (FileSystemPath: a drive's mount point, a file),
// or open another shell folder (ChildFolder: its parsing name, e.g. "shell:Network"), or be just a row.
public sealed record ShellItem(string Id, string Name, bool IsFolder, IReadOnlyList<string> IconNames)
{
    public string? Detail { get; init; }              // a second line: "93 GB free of 466 GB"
    public string? TypeText { get; init; }            // "Local disk"
    public long? Size { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public string? FileSystemPath { get; init; }
    public string? ChildFolder { get; init; }
    public string? Group { get; init; }               // the heading it sits under when a view groups by type ("Folders", "Devices and drives")
    public double? Usage { get; init; }               // 0..1 fullness, for a drive: views draw a bar under it
    public string? DefaultAction { get; init; }       // run by double-click when there is no path or child folder ("open")
}

public sealed record ShellAction(string Id, string Label, bool Dangerous = false);

// What a ShellFolder component implements. Register it as a component in ComponentCategories.ShellFolder with
// ThreadingModel.Apartment: shell folders are used from the UI thread only. Registration adds, next to the
// usual InprocServer32, a  CLSID\{guid}\ShellFolder  key with ParsingName (the name after "shell:") and Attributes.
public interface IShellFolder
{
    string DisplayName { get; }
    IReadOnlyList<string> IconNames { get; }
    IReadOnlyList<ShellItem> GetItems();

    // Actions for the item (null: for the folder itself, e.g. "Empty Trash").
    IReadOnlyList<ShellAction> GetActions(ShellItem? item);
    Task InvokeAsync(string actionId, ShellItem? item, CancellationToken cancel);

    // Raised when the contents changed on their own (a drive was mounted); the window then lists again.
    event EventHandler? Changed;
}
