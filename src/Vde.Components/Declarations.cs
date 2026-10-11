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
