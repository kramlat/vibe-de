namespace Vibe.Registry;

// A hive with other hives mounted as some of its top-level keys. HKLM\SAM is one: the key is not in the system
// hive file, it is served by PAMSAM. Paths below a mount go to the hive mounted there (its root is the mounted
// key); everything else goes to the base hive. A mount hides a key of the same name in the base hive.
public sealed class MountedHive : IHive, IDisposable
{
    private readonly IHive _base;
    private readonly Dictionary<string, IHive> _mounts = new(StringComparer.OrdinalIgnoreCase);

    public MountedHive(IHive baseHive, IReadOnlyDictionary<string, IHive> mounts)
    {
        _base = baseHive;
        foreach (var (name, hive) in mounts)
        {
            _mounts[name] = hive;
            var captured = name;
            hive.Changed += (_, c) => Changed?.Invoke(this, c with { KeyPath = c.KeyPath.Length == 0 ? captured : captured + "\\" + c.KeyPath });
        }
        _base.Changed += (_, c) => { if (!IsMounted(c.KeyPath)) Changed?.Invoke(this, c); };
    }

    public IHive Base => _base;
    public string Name => _base.Name;
    public event EventHandler<RegistryChange>? Changed;

    public bool KeyExists(string path) => Route(path, (h, p) => h.KeyExists(p), mountRoot: true);

    public void CreateKey(string path) => Route(path, (h, p) => { h.CreateKey(p); return 0; });
    public bool DeleteKey(string path) => Route(path, (h, p) => h.DeleteKey(p));

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var (mount, rest) = Split(path);
        if (mount is not null) return mount.GetSubKeyNames(rest);
        var names = _base.GetSubKeyNames(path).ToList();
        if (IsRoot(path))
            foreach (var name in _mounts.Keys.Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)))
                names.Add(name);
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RegistryKeyInfo GetKeyInfo(string path)
    {
        var (mount, rest) = Split(path);
        if (mount is not null) return mount.GetKeyInfo(rest);
        var info = _base.GetKeyInfo(path);
        return IsRoot(path) ? info with { SubKeyCount = GetSubKeyNames(path).Count } : info;
    }

    public void SetKeyFlags(string path, RegistryKeyFlags flags) => Route(path, (h, p) => { h.SetKeyFlags(p, flags); return 0; });
    public PosixPermissions GetKeyPermissions(string path) => Route(path, (h, p) => h.GetKeyPermissions(p));
    public void SetKeyPermissions(string path, PosixPermissions permissions) => Route(path, (h, p) => { h.SetKeyPermissions(p, permissions); return 0; });
    public RegistryValue? GetValue(string path, string name) => Route(path, (h, p) => h.GetValue(p, name));
    public IReadOnlyList<RegistryValue> GetValues(string path) => Route(path, (h, p) => h.GetValues(p));
    public PosixPermissions? GetValuePermissions(string path, string name) => Route(path, (h, p) => h.GetValuePermissions(p, name));
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) => Route(path, (h, p) => { h.SetValue(p, name, kind, data); return 0; });
    public void SetValuePermissions(string path, string name, PosixPermissions permissions) => Route(path, (h, p) => { h.SetValuePermissions(p, name, permissions); return 0; });
    public bool DeleteValue(string path, string name) => Route(path, (h, p) => h.DeleteValue(p, name));

    public void Dispose()
    {
        (_base as IDisposable)?.Dispose();
        foreach (var hive in _mounts.Values) (hive as IDisposable)?.Dispose();
    }

    private static bool IsRoot(string path) => path.Trim('\\').Length == 0;
    private bool IsMounted(string path) => Split(path).Item1 is not null;

    private (IHive?, string) Split(string path)
    {
        path = path.Trim('\\');
        var slash = path.IndexOf('\\');
        var first = slash < 0 ? path : path[..slash];
        return _mounts.TryGetValue(first, out var hive) && first.Length > 0 ? (hive, slash < 0 ? "" : path[(slash + 1)..]) : (null, path);
    }

    private T Route<T>(string path, Func<IHive, string, T> action, bool mountRoot = false)
    {
        var (mount, rest) = Split(path);
        return mount is null ? action(_base, path) : action(mount, rest);
    }
}
