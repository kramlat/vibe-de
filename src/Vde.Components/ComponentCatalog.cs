using Vibe.Registry;

namespace Vde.Components;

// A registered component and whether it may be loaded.
public sealed record ComponentRegistration(ComponentInfo Component, ExecutionTrust Trust);

// Reads registrations back for whoever hosts components (VExplore). Nothing here loads an assembly.
// A registration is trusted only if its InprocServer32 entry was marked executable by someone allowed to,
// and the assembly file itself is not writable by others and belongs to the caller or to root.
public sealed class ComponentCatalog(ClassesRoot hkcr, RegistryIdentity? identity = null)
{
    private readonly RegistryIdentity _identity = identity ?? RegistryIdentity.Current;

    public ComponentRegistration? Find(string clsid)
    {
        if (ComponentInfo.NormalizeGuid(clsid) is not { } id)
            return null;
        var server = $@"CLSID\{id}\{ComponentRegistrar.ServerKey}";
        if (hkcr.GetValue(server, "")?.Data is not string path)
            return null;

        var threading = Enum.TryParse<ThreadingModel>(hkcr.GetValue(server, "ThreadingModel")?.Data as string, out var model)
            ? model
            : ThreadingModel.Apartment;
        var progId = hkcr.GetValue($@"CLSID\{id}\ProgID", "")?.Data as string;
        var categories = Categories(id);

        var info = new ComponentInfo(
            id,
            hkcr.GetValue(server, "Class")?.Data as string ?? string.Empty,
            path,
            hkcr.GetValue(server, "Assembly")?.Data as string ?? string.Empty,
            threading,
            progId,
            hkcr.GetValue($@"CLSID\{id}", "")?.Data as string,
            categories);

        var entry = ExecutionTrust.Evaluate(hkcr.GetValuePermissions(server, ""), _identity);
        var trust = entry.IsTrusted ? ExecutionTrust.EvaluateFile(path, _identity) : entry;
        return new ComponentRegistration(info, trust);
    }

    public ComponentRegistration? FindByProgId(string progId)
        => hkcr.GetValue($@"{progId}\CLSID", "")?.Data is string clsid ? Find(clsid) : null;

    public IReadOnlyList<ComponentRegistration> All()
    {
        if (!hkcr.KeyExists("CLSID"))
            return [];
        return hkcr.GetSubKeyNames("CLSID")
            .Select(Find)
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderBy(r => r.Component.Clsid, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> Categories(string clsid)
    {
        var key = $@"CLSID\{clsid}\Implemented Categories";
        return hkcr.KeyExists(key) ? hkcr.GetSubKeyNames(key) : [];
    }
}
