using Vde.Components;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record ProviderInfo(ComponentRegistration Registration, bool IsAuthorized)
{
    public string Clsid => Registration.Component.Clsid;
    public string Name => Registration.Component.Description ?? Registration.Component.ClassName;
    // May be loaded only when the user authorized it AND the registration is trusted (see ExecutionTrust).
    public bool MayLoad => IsAuthorized && Registration.Trust.IsTrusted;
}

// Which launcher search plugins exist and which the user allowed. The authorization is a value in the user's
// own hive: it cannot be set by an installer through the system hive, so nothing runs in the launcher unless
// this user said yes to it.
public sealed class LauncherProviders(ClassesRoot classes, IHive userHive, RegistryIdentity? identity = null)
{
    public const string KeyPath = LauncherModel.KeyPath + @"\Providers";

    private readonly ComponentCatalog _catalog = new(classes, identity);

    public IReadOnlyList<ProviderInfo> List()
        => _catalog.All().Where(r => r.Component.Categories?.Contains(ComponentCategories.LauncherSearchProvider, StringComparer.OrdinalIgnoreCase) == true)
            .Select(r => new ProviderInfo(r, IsAuthorized(r.Component.Clsid))).ToList();

    public bool IsAuthorized(string clsid)
        => ComponentInfo.NormalizeGuid(clsid) is { } id
            && userHive.GetValue(KeyPath, id) is { Kind: RegistryValueKind.DWord } v && v.Data is uint flag && flag == 1;

    public void Authorize(string clsid) => Set(clsid, 1u);

    public void Revoke(string clsid)
    {
        if (ComponentInfo.NormalizeGuid(clsid) is { } id)
            userHive.DeleteValue(KeyPath, id);
    }

    private void Set(string clsid, uint value)
    {
        var id = ComponentInfo.NormalizeGuid(clsid) ?? throw new ArgumentException("Not a GUID.", nameof(clsid));
        userHive.SetValue(KeyPath, id, RegistryValueKind.DWord, value);
    }
}
