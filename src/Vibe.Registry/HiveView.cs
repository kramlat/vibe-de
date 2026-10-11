namespace Vibe.Registry;

// A hive seen as one particular identity: every call is checked against that identity, not the hive's own.
// This is what lets a daemon serve several clients from one Hive at the same time. Created with Hive.As.
internal sealed class HiveView(Hive hive, RegistryIdentity identity) : IHive
{
    public string Name => hive.Name;

    public event EventHandler<RegistryChange>? Changed
    {
        add => hive.Changed += value;
        remove => hive.Changed -= value;
    }

    public bool KeyExists(string path) { using var _ = hive.Impersonate(identity); return hive.KeyExists(path); }
    public void CreateKey(string path) { using var _ = hive.Impersonate(identity); hive.CreateKey(path); }
    public bool DeleteKey(string path) { using var _ = hive.Impersonate(identity); return hive.DeleteKey(path); }
    public IReadOnlyList<string> GetSubKeyNames(string path) { using var _ = hive.Impersonate(identity); return hive.GetSubKeyNames(path); }
    public RegistryKeyInfo GetKeyInfo(string path) { using var _ = hive.Impersonate(identity); return hive.GetKeyInfo(path); }
    public void SetKeyFlags(string path, RegistryKeyFlags flags) { using var _ = hive.Impersonate(identity); hive.SetKeyFlags(path, flags); }
    public PosixPermissions GetKeyPermissions(string path) { using var _ = hive.Impersonate(identity); return hive.GetKeyPermissions(path); }
    public void SetKeyPermissions(string path, PosixPermissions permissions) { using var _ = hive.Impersonate(identity); hive.SetKeyPermissions(path, permissions); }

    public RegistryValue? GetValue(string path, string name) { using var _ = hive.Impersonate(identity); return hive.GetValue(path, name); }
    public IReadOnlyList<RegistryValue> GetValues(string path) { using var _ = hive.Impersonate(identity); return hive.GetValues(path); }
    public PosixPermissions? GetValuePermissions(string path, string name) { using var _ = hive.Impersonate(identity); return hive.GetValuePermissions(path, name); }
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) { using var _ = hive.Impersonate(identity); hive.SetValue(path, name, kind, data); }
    public void SetValuePermissions(string path, string name, PosixPermissions permissions) { using var _ = hive.Impersonate(identity); hive.SetValuePermissions(path, name, permissions); }
    public bool DeleteValue(string path, string name) { using var _ = hive.Impersonate(identity); return hive.DeleteValue(path, name); }
}
