using Vibe.Registry;

namespace Vde.Components;

// Writes and removes a component's registration, in the COM layout under HKCR:
//   CLSID\{guid}                         (Default) = description
//   CLSID\{guid}\InprocServer32          (Default) = path of the assembly, ThreadingModel, Class, Assembly
//   CLSID\{guid}\ProgID                  (Default) = progid
//   CLSID\{guid}\Implemented Categories\{catid}
//   <progid>                             (Default) = description
//   <progid>\CLSID                       (Default) = {guid}
// The InprocServer32 value is marked executable (755): loading an assembly runs its code, so the entry is
// trusted only because someone deliberately registered it, the same rule as for shell commands.
public static class ComponentRegistrar
{
    public const string ServerKey = "InprocServer32";

    public static void Register(ClassesRoot hkcr, ComponentInfo component)
    {
        Unregister(hkcr, component.Clsid);   // start clean: a changed ProgID or category must not leave the old one behind

        var clsid = $@"CLSID\{component.Clsid}";
        hkcr.SetValue(clsid, "", RegistryValueKind.String, component.Description ?? component.ClassName);

        var server = $@"{clsid}\{ServerKey}";
        hkcr.SetValue(server, "", RegistryValueKind.String, component.AssemblyPath);
        hkcr.SetValue(server, "ThreadingModel", RegistryValueKind.String, component.ThreadingModel.ToString());
        hkcr.SetValue(server, "Class", RegistryValueKind.String, component.ClassName);
        hkcr.SetValue(server, "Assembly", RegistryValueKind.String, component.AssemblyName);
        var current = hkcr.GetValuePermissions(server, "")!.Value;
        hkcr.SetValuePermissions(server, "", current.WithMode(PosixPermissions.KeyDefault));   // 755

        if (component.ProgId is { Length: > 0 } progId)
        {
            hkcr.SetValue($@"{clsid}\ProgID", "", RegistryValueKind.String, progId);
            hkcr.SetValue(progId, "", RegistryValueKind.String, component.Description ?? component.ClassName);
            hkcr.SetValue($@"{progId}\CLSID", "", RegistryValueKind.String, component.Clsid);
        }

        foreach (var category in component.Categories ?? [])
            hkcr.CreateKey($@"{clsid}\Implemented Categories\{category}");
    }

    // True when something was registered. The ProgID is only removed if it still points at this component.
    public static bool Unregister(ClassesRoot hkcr, string clsid)
    {
        var key = $@"CLSID\{clsid}";
        if (!hkcr.KeyExists(key))
            return false;

        var progId = hkcr.GetValue($@"{key}\ProgID", "")?.Data as string;
        hkcr.DeleteKey(key);
        if (progId is { Length: > 0 }
            && hkcr.GetValue($@"{progId}\CLSID", "")?.Data is string target
            && string.Equals(target, clsid, StringComparison.OrdinalIgnoreCase))
            hkcr.DeleteKey(progId);
        return true;
    }
}
