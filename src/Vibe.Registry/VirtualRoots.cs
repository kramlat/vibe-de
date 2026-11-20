namespace Vibe.Registry;

// Roots that hold no keys of their own but show keys that live elsewhere, so tools can walk to them like any
// other root. HKCC and HKU below are such roots; HKCR (ClassesRoot) merges two hives and is its own class.

// A root that is a window onto one key of another hive: HKEY_CURRENT_CONFIG is HKLM\System\CurrentControlSet\Hardware Profiles\Current.
// Reads and writes go straight through; the root itself always exists, even before the key behind it does.
public sealed class HiveAlias : IHive
{
    private readonly IHive _target;
    private readonly string _basePath;

    public HiveAlias(string name, IHive target, string basePath)
    {
        Name = name;
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _basePath = basePath.Trim('\\');
        _target.Changed += Relay;
    }

    public string Name { get; }
    public event EventHandler<RegistryChange>? Changed;

    public bool KeyExists(string path) => IsRoot(path) || _target.KeyExists(Map(path));
    public void CreateKey(string path) => _target.CreateKey(Map(path));
    public bool DeleteKey(string path)
        => IsRoot(path) ? throw new UnauthorizedAccessException($"{Name} cannot be deleted.") : _target.DeleteKey(Map(path));

    public IReadOnlyList<string> GetSubKeyNames(string path)
        => IsRoot(path) && !_target.KeyExists(_basePath) ? [] : _target.GetSubKeyNames(Map(path));

    public RegistryKeyInfo GetKeyInfo(string path)
        => IsRoot(path) && !_target.KeyExists(_basePath)
            ? new RegistryKeyInfo(default, DateTime.UtcNow, DateTime.UtcNow, 0, 0, _target.GetKeyPermissions(""))
            : _target.GetKeyInfo(Map(path));

    public void SetKeyFlags(string path, RegistryKeyFlags flags) { _target.CreateKey(Map(path)); _target.SetKeyFlags(Map(path), flags); }
    public PosixPermissions GetKeyPermissions(string path)
        => IsRoot(path) && !_target.KeyExists(_basePath) ? _target.GetKeyPermissions("") : _target.GetKeyPermissions(Map(path));
    public void SetKeyPermissions(string path, PosixPermissions permissions) { _target.CreateKey(Map(path)); _target.SetKeyPermissions(Map(path), permissions); }

    public RegistryValue? GetValue(string path, string name) => IsRoot(path) && !_target.KeyExists(_basePath) ? null : _target.GetValue(Map(path), name);
    public IReadOnlyList<RegistryValue> GetValues(string path) => IsRoot(path) && !_target.KeyExists(_basePath) ? [] : _target.GetValues(Map(path));
    public PosixPermissions? GetValuePermissions(string path, string name) => IsRoot(path) && !_target.KeyExists(_basePath) ? null : _target.GetValuePermissions(Map(path), name);
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) => _target.SetValue(Map(path), name, kind, data);
    public void SetValuePermissions(string path, string name, PosixPermissions permissions) => _target.SetValuePermissions(Map(path), name, permissions);
    public bool DeleteValue(string path, string name) => !(IsRoot(path) && !_target.KeyExists(_basePath)) && _target.DeleteValue(Map(path), name);

    private static bool IsRoot(string path) => path.Trim('\\').Length == 0;

    private string Map(string path)
    {
        path = path.Trim('\\');
        return path.Length == 0 ? _basePath : _basePath.Length == 0 ? path : _basePath + "\\" + path;
    }

    private void Relay(object? sender, RegistryChange change)
    {
        if (_basePath.Length > 0 && !change.IsUnder(_basePath))
            return;
        var relative = change.KeyPath.Length > _basePath.Length ? change.KeyPath[(_basePath.Length == 0 ? 0 : _basePath.Length + 1)..] : "";
        if (relative.Length == 0 && change.Kind == RegistryChangeKind.KeyCreated)
            return;
        Changed?.Invoke(this, change with { KeyPath = relative });
    }
}

// HKEY_USERS: one subkey per user profile. Everybody sees ".DEFAULT" (the profile new users start from,
// HKLM\Users\.DEFAULT, created when something writes to it) and their own profile under their numeric uid
// (the same keys as HKCU). Only root also sees and can open the other users' profiles, which are their hive
// files found through /etc/passwd; for anyone else those do not exist here. Profiles cannot be added or
// removed from here.
public sealed class UsersRoot : IHive
{
    public const string DefaultProfile = ".DEFAULT";
    public const string DefaultProfilePath = @"Users\.DEFAULT";

    private readonly Dictionary<string, HiveAlias> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _privileged;
    private readonly Lazy<Dictionary<string, HiveAlias>> _others;

    // privileged says whether the caller is root (default: asked of the process); discover finds the other users'
    // hives by profile name when it is (default: the hive file of every user in /etc/passwd).
    public UsersRoot(IHive currentUser, IHive localMachine, string? userId = null, bool? privileged = null,
        Func<IReadOnlyDictionary<string, IHive>>? discover = null)
    {
        _privileged = privileged ?? RegistryIdentity.Current.IsRoot;
        var self = userId ?? RegistryIdentity.Current.Uid.ToString();
        _own[DefaultProfile] = new HiveAlias(DefaultProfile, localMachine, DefaultProfilePath);
        _own[self] = new HiveAlias("user", currentUser, "");
        foreach (var (name, alias) in _own)
            Relay(name, alias);
        _others = new(() =>
        {
            var found = new Dictionary<string, HiveAlias>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, hive) in (discover ?? DiscoverProfiles)())
                if (!_own.ContainsKey(name))
                    found[name] = new HiveAlias("user", hive, "");
            return found;
        });
    }

    public string Name => "HKU";
    public event EventHandler<RegistryChange>? Changed;

    // The root is a fixed listing: read and traverse for everyone, no changes.
    private static readonly PosixPermissions RootPermissions = new(0, 0, 0x16D);   // 0555

    // The hive file of every other user that has one, by numeric uid. Only ever called for root.
    public static IReadOnlyDictionary<string, IHive> DiscoverProfiles()
    {
        var found = new Dictionary<string, IHive>();
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length < 6 || f[5].Length == 0 || !uint.TryParse(f[2], out var uid))
                    continue;
                var file = Path.Combine(f[5], ".config", "hive", "User.fhive");
                if (File.Exists(file))
                    try { found[uid.ToString()] = Hive.OpenShared("HKU\\" + uid, file, watch: false); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        catch (IOException) { }
        return found;
    }

    private void Relay(string name, HiveAlias alias)
        => alias.Changed += (_, c) => Changed?.Invoke(this, c with { KeyPath = c.KeyPath.Length == 0 ? name : name + "\\" + c.KeyPath });

    private Dictionary<string, HiveAlias> Profiles()
    {
        if (!_privileged)
            return _own;
        var all = new Dictionary<string, HiveAlias>(_own, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, alias) in _others.Value)
            all[name] = alias;
        return all;
    }

    public bool KeyExists(string path)
        => Split(path) is var (profile, rest) && (profile is null || (Profiles().TryGetValue(profile, out var p) && p.KeyExists(rest)));

    public void CreateKey(string path)
    {
        var (profile, rest) = Split(path);
        Alias(profile).CreateKey(rest);
    }

    public bool DeleteKey(string path)
    {
        var (profile, rest) = Split(path);
        return rest.Length == 0 ? throw new UnauthorizedAccessException("User profiles cannot be removed from here.") : Alias(profile).DeleteKey(rest);
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
        => IsRoot(path) ? Profiles().Keys.Order(StringComparer.OrdinalIgnoreCase).ToList() : Route(path, (a, r) => a.GetSubKeyNames(r));

    public RegistryKeyInfo GetKeyInfo(string path)
        => IsRoot(path) ? new RegistryKeyInfo(default, DateTime.UtcNow, DateTime.UtcNow, Profiles().Count, 0, RootPermissions) : Route(path, (a, r) => a.GetKeyInfo(r));

    public void SetKeyFlags(string path, RegistryKeyFlags flags) => Route(path, (a, r) => { a.SetKeyFlags(r, flags); return 0; });
    public PosixPermissions GetKeyPermissions(string path) => IsRoot(path) ? RootPermissions : Route(path, (a, r) => a.GetKeyPermissions(r));
    public void SetKeyPermissions(string path, PosixPermissions permissions) => Route(path, (a, r) => { a.SetKeyPermissions(r, permissions); return 0; });

    public RegistryValue? GetValue(string path, string name) => IsRoot(path) ? null : Route(path, (a, r) => a.GetValue(r, name));
    public IReadOnlyList<RegistryValue> GetValues(string path) => IsRoot(path) ? [] : Route(path, (a, r) => a.GetValues(r));
    public PosixPermissions? GetValuePermissions(string path, string name) => IsRoot(path) ? null : Route(path, (a, r) => a.GetValuePermissions(r, name));
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) => Route(path, (a, r) => { a.SetValue(r, name, kind, data); return 0; });
    public void SetValuePermissions(string path, string name, PosixPermissions permissions) => Route(path, (a, r) => { a.SetValuePermissions(r, name, permissions); return 0; });
    public bool DeleteValue(string path, string name) => !IsRoot(path) && Route(path, (a, r) => a.DeleteValue(r, name));

    private static bool IsRoot(string path) => path.Trim('\\').Length == 0;

    private static (string? Profile, string Remainder) Split(string path)
    {
        path = path.Trim('\\');
        if (path.Length == 0)
            return (null, "");
        var slash = path.IndexOf('\\');
        return slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
    }

    private HiveAlias Alias(string? profile)
        => profile is null ? throw new UnauthorizedAccessException("HKEY_USERS holds only user profiles; values cannot be stored here.")
            : Profiles().TryGetValue(profile, out var a) ? a
            : throw new UnauthorizedAccessException($"There is no profile '{profile}' you can open here: profiles cannot be added from here, and other users' profiles are for root.");

    private T Route<T>(string path, Func<HiveAlias, string, T> action)
    {
        var (profile, rest) = Split(path);
        return action(Alias(profile), rest);
    }
}

// A root that exists but holds nothing: HKEY_PERFORMANCE_DATA and HKEY_DYN_DATA, kept as handles for
// compatibility only (programs that ask for them get an empty, read-only root). Tools such as the registry
// editor do not list them, as Windows' own does not.
public sealed class EmptyRoot(string name) : IHive
{
    public const string PerformanceDataName = "HKPD";
    public const string DynDataName = "HKDD";

    private static readonly PosixPermissions Permissions = new(0, 0, 0x16D);   // 0555: look, don't touch

    public string Name => name;
    public event EventHandler<RegistryChange>? Changed { add { } remove { } }

    public bool KeyExists(string path) => IsRoot(path);
    public IReadOnlyList<string> GetSubKeyNames(string path) => IsRoot(path) ? [] : throw Missing(path);
    public RegistryKeyInfo GetKeyInfo(string path) => IsRoot(path) ? new RegistryKeyInfo(default, DateTime.UtcNow, DateTime.UtcNow, 0, 0, Permissions) : throw Missing(path);
    public PosixPermissions GetKeyPermissions(string path) => IsRoot(path) ? Permissions : throw Missing(path);
    public RegistryValue? GetValue(string path, string valueName) => null;
    public IReadOnlyList<RegistryValue> GetValues(string path) => IsRoot(path) ? [] : throw Missing(path);
    public PosixPermissions? GetValuePermissions(string path, string valueName) => null;

    public void CreateKey(string path) => throw ReadOnly();
    public bool DeleteKey(string path) => throw ReadOnly();
    public void SetKeyFlags(string path, RegistryKeyFlags flags) => throw ReadOnly();
    public void SetKeyPermissions(string path, PosixPermissions permissions) => throw ReadOnly();
    public void SetValue(string path, string valueName, RegistryValueKind kind, object? data) => throw ReadOnly();
    public void SetValuePermissions(string path, string valueName, PosixPermissions permissions) => throw ReadOnly();
    public bool DeleteValue(string path, string valueName) => throw ReadOnly();

    private static bool IsRoot(string path) => path.Trim('\\').Length == 0;
    private static KeyNotFoundException Missing(string path) => new($"Key '{path}' does not exist.");
    private UnauthorizedAccessException ReadOnly() => new($"{name} is empty and cannot be changed.");
}
