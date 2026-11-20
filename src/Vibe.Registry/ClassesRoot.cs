namespace Vibe.Registry;

// HKEY_CLASSES_ROOT: a merged view of Software\Classes in the system hive (HKLM) and the user hive (HKCU).
// Reads see both, with the user's values winning; writes and deletes only ever touch the user hive.
public sealed class ClassesRoot : IHive
{
    public const string KeyPath = @"Software\Classes";

    private readonly IHive _user;
    private readonly IHive? _system;

    // Same shape as Hive.Changed, with paths relative to HKCR.
    public event EventHandler<RegistryChange>? Changed;

    public ClassesRoot(IHive user, IHive? system = null)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _system = system;
        _user.Changed += Relay;
        if (_system is not null)
            _system.Changed += Relay;
    }

    public string Name => "HKCR";

    // The root itself always exists, even before anyone has registered a class.
    public bool KeyExists(string path)
        => IsRoot(path) || _user.KeyExists(Map(path)) || (_system?.KeyExists(Map(path)) ?? false);

    // Counts are of the merged view; flags, times and permissions are those of the key that wins (the user's).
    public RegistryKeyInfo GetKeyInfo(string path)
    {
        var winner = Hives().FirstOrDefault(h => h.KeyExists(Map(path)));
        if (winner is null)
            return IsRoot(path)
                ? new RegistryKeyInfo(default, DateTime.UtcNow, DateTime.UtcNow, 0, 0, _user.GetKeyPermissions(""))
                : throw new KeyNotFoundException($"Key '{path}' does not exist.");
        var info = winner.GetKeyInfo(Map(path));
        return info with { SubKeyCount = GetSubKeyNames(path).Count, ValueCount = GetValues(path).Count };
    }

    public PosixPermissions GetKeyPermissions(string path)
        => (Hives().FirstOrDefault(h => h.KeyExists(Map(path))) ?? _user).GetKeyPermissions(Map(path));

    // Changing a key that only the system hive has first overrides it in the user hive.
    public void SetKeyFlags(string path, RegistryKeyFlags flags)
    {
        _user.CreateKey(Map(path));
        _user.SetKeyFlags(Map(path), flags);
    }

    public void SetKeyPermissions(string path, PosixPermissions permissions)
    {
        _user.CreateKey(Map(path));
        _user.SetKeyPermissions(Map(path), permissions);
    }

    private static bool IsRoot(string path) => path.Trim('\\').Length == 0;

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool found = false;
        foreach (var hive in Hives())
            if (hive.KeyExists(Map(path)))
            {
                found = true;
                names.UnionWith(hive.GetSubKeyNames(Map(path)));
            }
        return found || IsRoot(path) ? names.ToList() : throw new KeyNotFoundException($"Key '{path}' does not exist.");
    }

    public RegistryValue? GetValue(string path, string name)
    {
        foreach (var hive in Hives())
            if (hive.GetValue(Map(path), name) is { } value)
                return value;
        return null;
    }

    // The permissions of the value that GetValue would return: the user's if they have one, else the system's.
    public PosixPermissions? GetValuePermissions(string path, string name)
    {
        foreach (var hive in Hives())
            if (hive.GetValuePermissions(Map(path), name) is { } permissions)
                return permissions;
        return null;
    }

    // Value names from both hives; where both have one, the user's.
    public IReadOnlyList<RegistryValue> GetValues(string path)
    {
        var merged = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        bool found = false;
        foreach (var hive in Hives().Reverse())   // system first so the user overwrites
            if (hive.KeyExists(Map(path)))
            {
                found = true;
                foreach (var value in hive.GetValues(Map(path)))
                    merged[value.Name] = value;
            }
        return found || IsRoot(path)
            ? merged.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : throw new KeyNotFoundException($"Key '{path}' does not exist.");
    }

    // Changes the owner, group or mode of a value in the user's hive (a value that only the system hive has
    // cannot be changed from here: override it in the user hive first).
    public void SetValuePermissions(string path, string name, PosixPermissions permissions)
        => _user.SetValuePermissions(Map(path), name, permissions);

    public void CreateKey(string path) => _user.CreateKey(Map(path));
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) => _user.SetValue(Map(path), name, kind, data);
    public bool DeleteValue(string path, string name) => _user.DeleteValue(Map(path), name);
    public bool DeleteKey(string path) => _user.DeleteKey(Map(path));

    private IEnumerable<IHive> Hives()
    {
        yield return _user;
        if (_system is not null)
            yield return _system;
    }

    private static string Map(string path)
    {
        path = path.Trim('\\');
        return path.Length == 0 ? KeyPath : KeyPath + "\\" + path;
    }

    private void Relay(object? sender, RegistryChange change)
    {
        if (!change.IsUnder(KeyPath))
            return;
        var relative = change.KeyPath.Length > KeyPath.Length ? change.KeyPath[(KeyPath.Length + 1)..] : string.Empty;
        if (relative.Length == 0 && change.Kind == RegistryChangeKind.KeyCreated)
            return;   // HKCR itself always exists as a view; its backing key appearing is not news
        Changed?.Invoke(this, change with { KeyPath = relative });
    }
}
