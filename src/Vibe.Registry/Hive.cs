namespace Vibe.Registry;

[Flags]
public enum RegistryKeyFlags : uint
{
    None = 0,
    System = 1 << 0,
    Hidden = 1 << 1,
    Volatile = 1 << 2,   // never written to disk
    Policy = 1 << 3,
}

public enum RegistryChangeKind
{
    KeyCreated,
    KeyDeleted,
    KeyFlagsChanged,
    ValueSet,
    ValueDeleted,
}

public sealed record RegistryChange(RegistryChangeKind Kind, string KeyPath, string? ValueName = null)
{
    // True when this change happened at or below the given key path.
    public bool IsUnder(string path)
    {
        path = path.Trim('\\');
        return path.Length == 0
            || KeyPath.Equals(path, StringComparison.OrdinalIgnoreCase)
            || KeyPath.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RegistryKeyInfo(
    RegistryKeyFlags Flags, DateTime CreatedUtc, DateTime ModifiedUtc, int SubKeyCount, int ValueCount);

// One registry hive: a tree of keys holding typed values. Paths use '\' and are case-insensitive;
// the empty path is the hive root. Safe to use from several threads.
public sealed class Hive
{
    private readonly object _gate = new();
    private readonly Node _root;
    private volatile bool _dirty;

    public string Name { get; }
    public string? FilePath { get; set; }
    public bool IsDirty => _dirty;

    // Raised on the calling thread after the change is applied and the lock released.
    public event EventHandler<RegistryChange>? Changed;

    public Hive(string name) : this(name, new Node(string.Empty, null)) { }

    private Hive(string name, Node root)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _root = root;
    }

    // ---- persistence ----

    // Opens the file if it exists, otherwise returns an empty hive that will save there.
    public static Hive Load(string name, string path)
    {
        Hive hive;
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            hive = Read(name, stream);
        }
        else
        {
            hive = new Hive(name);
        }
        hive.FilePath = path;
        return hive;
    }

    public static Hive Read(string name, Stream stream) => new(name, FhiveFormat.Read(stream));

    public void Write(Stream stream)
    {
        lock (_gate)
            FhiveFormat.Write(_root, stream);
    }

    public void Save()
        => Save(FilePath ?? throw new InvalidOperationException("The hive has no file path."));

    // Writes to a temporary file and renames it over the target, so a crash never leaves half a hive.
    public void Save(string path)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            Write(stream);
        File.Move(temp, path, overwrite: true);

        if (path == FilePath)
            _dirty = false;
    }

    // ---- keys ----

    public bool KeyExists(string path)
    {
        var parts = Split(path);
        lock (_gate)
            return Find(parts) is not null;
    }

    public void CreateKey(string path)
    {
        var parts = Split(path);
        Mutate<object?>(changes =>
        {
            Ensure(parts, changes);
            return null;
        });
    }

    // Deletes a key and everything below it. Returns false if it did not exist.
    public bool DeleteKey(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            throw new ArgumentException("The root key cannot be deleted.", nameof(path));

        return Mutate(changes =>
        {
            var node = Find(parts);
            if (node is null)
                return false;
            node.Parent!.Children.Remove(node.Name);
            node.Parent.Modified = DateTime.UtcNow;
            changes.Add(new RegistryChange(RegistryChangeKind.KeyDeleted, node.FullPath));
            return true;
        });
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var parts = Split(path);
        lock (_gate)
            return Require(parts).Children.Values
                .Select(c => c.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public RegistryKeyInfo GetKeyInfo(string path)
    {
        var parts = Split(path);
        lock (_gate)
        {
            var node = Require(parts);
            return new RegistryKeyInfo(node.Flags, node.Created, node.Modified, node.Children.Count, node.Values.Count);
        }
    }

    public void SetKeyFlags(string path, RegistryKeyFlags flags)
    {
        var parts = Split(path);
        Mutate<object?>(changes =>
        {
            var node = Ensure(parts, changes);
            if (node.Flags != flags)
            {
                node.Flags = flags;
                node.Modified = DateTime.UtcNow;
                changes.Add(new RegistryChange(RegistryChangeKind.KeyFlagsChanged, node.FullPath));
            }
            return null;
        });
    }

    // ---- values ----

    // The default value of a key has the empty name.
    public RegistryValue? GetValue(string path, string name)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
            return Find(parts)?.Values.GetValueOrDefault(name);
    }

    public IReadOnlyList<RegistryValue> GetValues(string path)
    {
        var parts = Split(path);
        lock (_gate)
            return Require(parts).Values.Values
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    // Creates the key if needed. Setting an identical value is a no-op and raises no event.
    public void SetValue(string path, string name, RegistryValueKind kind, object? data)
    {
        var parts = Split(path);
        var value = new RegistryValue(name, kind, data);

        Mutate<object?>(changes =>
        {
            var node = Ensure(parts, changes);
            if (node.Values.TryGetValue(name, out var existing) && existing.SameAs(value))
                return null;
            node.Values[name] = value;
            node.Modified = DateTime.UtcNow;
            changes.Add(new RegistryChange(RegistryChangeKind.ValueSet, node.FullPath, name));
            return null;
        });
    }

    public bool DeleteValue(string path, string name)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);

        return Mutate(changes =>
        {
            var node = Find(parts);
            if (node is null || !node.Values.Remove(name))
                return false;
            node.Modified = DateTime.UtcNow;
            changes.Add(new RegistryChange(RegistryChangeKind.ValueDeleted, node.FullPath, name));
            return true;
        });
    }

    // ---- internals ----

    private T Mutate<T>(Func<List<RegistryChange>, T> body)
    {
        var changes = new List<RegistryChange>();
        T result;
        lock (_gate)
        {
            result = body(changes);
            if (changes.Count > 0)
                _dirty = true;
        }
        foreach (var change in changes)
            Changed?.Invoke(this, change);
        return result;
    }

    private static string[] Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = path.Trim('\\');
        if (path.Length == 0)
            return [];

        var parts = path.Split('\\');
        if (parts.Any(p => p.Length == 0))
            throw new ArgumentException($"Invalid key path '{path}'.", nameof(path));
        return parts;
    }

    private Node? Find(string[] parts)
    {
        var node = _root;
        foreach (var part in parts)
            if (!node.Children.TryGetValue(part, out node))
                return null;
        return node;
    }

    private Node Require(string[] parts)
        => Find(parts) ?? throw new KeyNotFoundException($"Key '{string.Join('\\', parts)}' does not exist.");

    private Node Ensure(string[] parts, List<RegistryChange> changes)
    {
        var node = _root;
        foreach (var part in parts)
        {
            if (!node.Children.TryGetValue(part, out var child))
            {
                child = new Node(part, node);
                node.Children.Add(part, child);
                node.Modified = DateTime.UtcNow;
                changes.Add(new RegistryChange(RegistryChangeKind.KeyCreated, child.FullPath));
            }
            node = child;
        }
        return node;
    }
}
