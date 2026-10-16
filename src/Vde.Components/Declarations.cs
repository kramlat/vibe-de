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

// ---- property sheets: what a component implements to add a tab to a file's Properties window ----

// What the property sheet is about: the files and folders it was opened for.
public sealed record PropertyTarget(IReadOnlyList<string> Paths, string MimeType)
{
    public bool IsSingle => Paths.Count == 1;
}

// One extra tab. CreateContent returns an Avalonia Control (typed as object so this assembly needs no UI
// dependency); the sheet calls it on the UI thread when the tab is first shown. Apply is called when the user
// presses OK or Apply, on the UI thread, and returns false to keep the sheet open (after explaining why).
public sealed record PropertyPageInfo(string Title, Func<object> CreateContent, Func<bool>? Apply = null, int Order = 100);

// Implemented by components registered under  <class>\shellex\PropertySheetHandlers\<name>  (default value =
// the component's CLSID), where <class> is "*" (every file), "Directory", an extension such as ".png", or a ProgID.
public interface IPropertyPageProvider
{
    IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target);
}
