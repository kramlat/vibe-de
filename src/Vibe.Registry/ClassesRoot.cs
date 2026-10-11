namespace Vibe.Registry;

// HKEY_CLASSES_ROOT: a merged view of Software\Classes in the system hive (HKLM) and the user hive (HKCU).
// Reads see both, with the user's values winning; writes and deletes only ever touch the user hive.
public sealed class ClassesRoot
{
    public const string KeyPath = @"Software\Classes";

    private readonly Hive _user;
    private readonly Hive? _system;

    // Same shape as Hive.Changed, with paths relative to HKCR.
    public event EventHandler<RegistryChange>? Changed;

    public ClassesRoot(Hive user, Hive? system = null)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _system = system;
        _user.Changed += Relay;
        if (_system is not null)
            _system.Changed += Relay;
    }

    public bool KeyExists(string path)
        => _user.KeyExists(Map(path)) || (_system?.KeyExists(Map(path)) ?? false);

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
        return found ? names.ToList() : throw new KeyNotFoundException($"Key '{path}' does not exist.");
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
        return found
            ? merged.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : throw new KeyNotFoundException($"Key '{path}' does not exist.");
    }

    public void CreateKey(string path) => _user.CreateKey(Map(path));
    public void SetValue(string path, string name, RegistryValueKind kind, object? data) => _user.SetValue(Map(path), name, kind, data);
    public bool DeleteValue(string path, string name) => _user.DeleteValue(Map(path), name);
    public bool DeleteKey(string path) => _user.DeleteKey(Map(path));

    private IEnumerable<Hive> Hives()
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
