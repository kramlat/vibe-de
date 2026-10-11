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
    PermissionsChanged,   // owner, group or mode of a key (ValueName null) or of a value
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
    RegistryKeyFlags Flags, DateTime CreatedUtc, DateTime ModifiedUtc, int SubKeyCount, int ValueCount,
    PosixPermissions Permissions);

// One registry hive: a tree of keys holding typed values. Paths use '\' and are case-insensitive;
// the empty path is the hive root. Safe to use from several threads.
//
// Every key and value has an owner, group and rwxrwxrwx mode, checked against Identity on every call
// (see PosixPermissions for what each bit means). Denied calls throw UnauthorizedAccessException.
public sealed class Hive : IDisposable
{
    private readonly object _gate = new();
    private Node _root;
    private volatile bool _dirty;
    private HiveFile? _file;          // set for hives opened with OpenShared
    private IDisposable? _watching;

    public string Name { get; }
    public string? FilePath { get; set; }
    public bool IsDirty => _dirty;

    // Who is asking. Defaults to the user and groups of this process; a service acting for someone
    // else sets it (a Regedit run through pkexec simply is root).
    public RegistryIdentity Identity { get; set; } = RegistryIdentity.Current;

    // Bits removed from the default modes of new keys (0755) and values (0644), as the shell's umask does.
    public ushort Umask { get; set; }

    // Raised on the calling thread after the change is applied and the lock released.
    public event EventHandler<RegistryChange>? Changed;

    public Hive(string name) : this(name, () => DateTime.UtcNow)
    {
    }

    // clock supplies every timestamp (created, modified); a fixed one makes a hive file reproducible.
    public Hive(string name, Func<DateTime> clock)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Clock = clock;
        _root = NewRoot();
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private Hive(string name, Node root)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _root = root;
    }

    private Node NewRoot() => new(string.Empty, null)
    {
        Perms = new PosixPermissions(Identity.Uid, Identity.Gid, PosixPermissions.KeyDefault),
        Created = Clock(),
        Modified = Clock(),
    };

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

    // Opens a hive that several processes use at once (see HiveFile for the protocol). Every change is saved
    // immediately under a file lock, so there is nothing to Save and no update is lost; changes other processes
    // make are picked up by a file watcher and raised as Changed events. A read-only hive (the system hive,
    // for ordinary users) never locks or writes, and any change to it throws UnauthorizedAccessException.
    public static Hive OpenShared(string name, string path, bool watch = true, bool readOnly = false, UnixFileMode? fileMode = null)
    {
        var hive = new Hive(name) { FilePath = path };
        var file = new HiveFile(path, readOnly, fileMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        hive._file = file;

        if (readOnly)
        {
            // Never creates anything: a hive that does not exist reads as empty.
            if (file.ReadIfChanged(hive.NewRoot) is { } existing)
                hive._root = existing;
        }
        else
        {
            // A hive that does not exist is created now, empty, so it is there for every other process too.
            using (file.Lock())
            {
                if (file.ReadIfChanged(hive.NewRoot) is { } existing)
                    hive._root = existing;
                if (!File.Exists(file.Path))
                    file.Write(hive._root);
            }
        }

        if (watch)
            hive._watching = file.Watch(hive.RefreshQuietly);
        return hive;
    }

    public bool IsShared => _file is not null;
    public bool IsReadOnly => _file?.ReadOnly ?? false;

    // Re-reads a shared hive if another process changed it, raising Changed for each difference.
    public void Refresh()
    {
        var changes = new List<RegistryChange>();
        lock (_gate)
        {
            if (_file is null)
                return;
            ApplyExternal(changes);
        }
        foreach (var change in changes)
            Changed?.Invoke(this, change);
    }

    // The watcher calls this on a thread-pool thread: a half-written or broken file must not kill the process.
    private void RefreshQuietly()
    {
        try
        {
            Refresh();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }
    }

    // Caller holds _gate.
    private void ApplyExternal(List<RegistryChange> changes)
    {
        if (_file!.ReadIfChanged(NewRoot) is not { } root)
            return;
        HiveDiff.GraftVolatile(_root, root);
        changes.AddRange(HiveDiff.Between(_root, root));
        _root = root;
    }

    public void Dispose()
    {
        _watching?.Dispose();
        _watching = null;
    }

    public static Hive Read(string name, Stream stream) => new(name, FhiveFormat.Read(stream));

    public void Write(Stream stream)
    {
        lock (_gate)
            FhiveFormat.Write(_root, stream);
    }

    public void Save()
    {
        if (_file is not null)
            return;   // shared hives are already on disk
        Save(FilePath ?? throw new InvalidOperationException("The hive has no file path."));
    }

    // Writes to a temporary file and renames it over the target, so a crash never leaves half a hive.
    // The file keeps its permission (a 0600 hive stays 0600); a new one is private (0600) unless made otherwise.
    public void Save(string path)
    {
        var temp = path + ".tmp";
        var mode = File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = mode,
               }))
        {
            Write(stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);

        if (path == FilePath)
            _dirty = false;
    }

    // ---- keys ----

    // Like stat: needs search permission on the keys leading to it, and is false (not an error) when that is
    // missing, so what lies inside a closed key cannot be probed.
    public bool KeyExists(string path)
    {
        var parts = Split(path);
        lock (_gate)
        {
            try
            {
                return Find(parts) is not null;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
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
    // Needs write+search on the parent, and, as rm -r does, write+search on every key being removed.
    public bool DeleteKey(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
            throw new ArgumentException("The root key cannot be deleted.", nameof(path));

        return Mutate(changes =>
        {
            var parent = Find(parts[..^1]);
            if (parent is null)
                return false;
            Demand(parent, Access.Execute, "search");
            if (!parent.Children.TryGetValue(parts[^1], out var node))
                return false;

            Demand(parent, Access.Write | Access.Execute, "delete keys from");
            DemandRemovable(node);

            parent.Children.Remove(node.Name);
            parent.Modified = Clock();
            changes.Add(new RegistryChange(RegistryChangeKind.KeyDeleted, node.FullPath));
            return true;
        });
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var parts = Split(path);
        lock (_gate)
        {
            var node = Require(parts);
            Demand(node, Access.Read, "list");
            return node.Children.Values
                .Select(c => c.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public RegistryKeyInfo GetKeyInfo(string path)
    {
        var parts = Split(path);
        lock (_gate)
        {
            var node = Require(parts);
            return new RegistryKeyInfo(node.Flags, node.Created, node.Modified, node.Children.Count, node.Values.Count, node.Perms);
        }
    }

    // Only the owner (or root) may change flags.
    public void SetKeyFlags(string path, RegistryKeyFlags flags)
    {
        var parts = Split(path);
        Mutate<object?>(changes =>
        {
            var node = Ensure(parts, changes);
            if (node.Flags != flags)
            {
                DemandOwner(node.Perms, "change the flags of");
                node.Flags = flags;
                node.Modified = Clock();
                changes.Add(new RegistryChange(RegistryChangeKind.KeyFlagsChanged, node.FullPath));
            }
            return null;
        });
    }

    public PosixPermissions GetKeyPermissions(string path)
    {
        var parts = Split(path);
        lock (_gate)
            return Require(parts).Perms;
    }

    // chmod, chgrp and chown in one: only the owner may change the mode, only root may change the owner,
    // and the owner may hand the key to a group they belong to. Applies to this key only, not below it.
    public void SetKeyPermissions(string path, PosixPermissions permissions)
    {
        var parts = Split(path);
        Mutate<object?>(changes =>
        {
            var node = Find(parts) ?? throw new KeyNotFoundException($"Key '{path}' does not exist.");
            if (CheckPermissionChange(node.Perms, permissions))
            {
                node.Perms = permissions;
                node.Modified = Clock();
                changes.Add(new RegistryChange(RegistryChangeKind.PermissionsChanged, node.FullPath));
            }
            return null;
        });
    }

    // ---- values ----

    // The default value of a key has the empty name. Null when there is no such value.
    public RegistryValue? GetValue(string path, string name)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            var node = Find(parts);
            if (node is null)
                return null;
            Demand(node, Access.Execute, "search");
            if (!node.Values.TryGetValue(name, out var value))
                return null;
            DemandValue(value, Access.Read, "read");
            return value;
        }
    }

    // The values you may read; ones you may not are left out, as ls shows names but cat is refused.
    public IReadOnlyList<RegistryValue> GetValues(string path)
    {
        var parts = Split(path);
        lock (_gate)
        {
            var node = Require(parts);
            Demand(node, Access.Read | Access.Execute, "list the values of");
            return node.Values.Values
                .Where(v => Identity.Allows(v.Permissions, Access.Read))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // Permissions of one value, or null when it does not exist.
    public PosixPermissions? GetValuePermissions(string path, string name)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            var node = Find(parts);
            if (node is null)
                return null;
            Demand(node, Access.Execute, "search");
            return node.Values.TryGetValue(name, out var value) ? value.Permissions : null;
        }
    }

    // Creates the key if needed. Setting an identical value is a no-op and raises no event.
    // A new value belongs to the caller; replacing one keeps its owner and mode.
    public void SetValue(string path, string name, RegistryValueKind kind, object? data)
    {
        var parts = Split(path);
        var value = new RegistryValue(name, kind, data);

        Mutate<object?>(changes =>
        {
            var node = Ensure(parts, changes);
            Demand(node, Access.Execute, "search");

            if (node.Values.TryGetValue(name, out var existing))
            {
                if (existing.SameAs(value))
                    return null;
                DemandValue(existing, Access.Write, "change");
                value = value.WithPermissions(existing.Permissions);
            }
            else
            {
                Demand(node, Access.Write | Access.Execute, "add values to");
                value = value.WithPermissions(NewPermissions(PosixPermissions.ValueDefault));
            }

            node.Values[name] = value;
            node.Modified = Clock();
            changes.Add(new RegistryChange(RegistryChangeKind.ValueSet, node.FullPath, name));
            return null;
        });
    }

    public void SetValuePermissions(string path, string name, PosixPermissions permissions)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);

        Mutate<object?>(changes =>
        {
            var node = Find(parts) ?? throw new KeyNotFoundException($"Key '{path}' does not exist.");
            Demand(node, Access.Execute, "search");
            if (!node.Values.TryGetValue(name, out var value))
                throw new KeyNotFoundException($"Value '{name}' does not exist in '{path}'.");

            if (CheckPermissionChange(value.Permissions, permissions))
            {
                node.Values[name] = value.WithPermissions(permissions);
                node.Modified = Clock();
                changes.Add(new RegistryChange(RegistryChangeKind.PermissionsChanged, node.FullPath, name));
            }
            return null;
        });
    }

    // Like deleting a file: it is the key's write permission that matters, not the value's.
    public bool DeleteValue(string path, string name)
    {
        var parts = Split(path);
        ArgumentNullException.ThrowIfNull(name);

        return Mutate(changes =>
        {
            var node = Find(parts);
            if (node is null)
                return false;
            Demand(node, Access.Execute, "search");
            if (!node.Values.ContainsKey(name))
                return false;

            Demand(node, Access.Write | Access.Execute, "remove values from");
            node.Values.Remove(name);
            node.Modified = Clock();
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
            if (_file is null)
            {
                result = body(changes);
                if (changes.Count > 0)
                    _dirty = true;
            }
            else
            {
                if (_file.ReadOnly)
                    throw new UnauthorizedAccessException($"The hive '{Name}' is read-only.");

                // lock the file, catch up with whatever other processes did, make the change, write it back
                using (_file.Lock())
                {
                    ApplyExternal(changes);
                    var local = new List<RegistryChange>();
                    result = body(local);
                    if (local.Count > 0)
                        _file.Write(_root);
                    changes.AddRange(local);
                }
            }
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

    // Walking down a path needs search (execute) on every key passed through, as with directories.
    private Node? Find(string[] parts)
    {
        var node = _root;
        foreach (var part in parts)
        {
            Demand(node, Access.Execute, "search");
            if (!node.Children.TryGetValue(part, out node))
                return null;
        }
        return node;
    }

    private Node Require(string[] parts)
        => Find(parts) ?? throw new KeyNotFoundException($"Key '{string.Join('\\', parts)}' does not exist.");

    // Creating a key needs write+search on the key it goes into; the new key belongs to the caller.
    private Node Ensure(string[] parts, List<RegistryChange> changes)
    {
        var node = _root;
        foreach (var part in parts)
        {
            Demand(node, Access.Execute, "search");
            if (!node.Children.TryGetValue(part, out var child))
            {
                Demand(node, Access.Write | Access.Execute, "create keys in");
                child = new Node(part, node) { Perms = NewPermissions(PosixPermissions.KeyDefault), Created = Clock(), Modified = Clock() };
                node.Children.Add(part, child);
                node.Modified = Clock();
                changes.Add(new RegistryChange(RegistryChangeKind.KeyCreated, child.FullPath));
            }
            node = child;
        }
        return node;
    }

    private PosixPermissions NewPermissions(ushort defaultMode)
        => new(Identity.Uid, Identity.Gid, (ushort)(defaultMode & ~Umask & PosixPermissions.Mask));

    private void Demand(Node node, Access wanted, string what)
    {
        if (!Identity.Allows(node.Perms, wanted))
            throw new UnauthorizedAccessException($"Permission denied: cannot {what} '{Describe(node)}' (mode {node.Perms.ModeText}, owner {node.Perms.Uid}).");
    }

    private void DemandValue(RegistryValue value, Access wanted, string what)
    {
        if (!Identity.Allows(value.Permissions, wanted))
            throw new UnauthorizedAccessException($"Permission denied: cannot {what} value '{value.Name}' (mode {value.Permissions.ModeText}, owner {value.Permissions.Uid}).");
    }

    private void DemandOwner(PosixPermissions permissions, string what)
    {
        if (!Identity.IsRoot && Identity.Uid != permissions.Uid)
            throw new UnauthorizedAccessException($"Permission denied: only the owner may {what} this object.");
    }

    private static string Describe(Node node) => node.Parent is null ? "(root)" : node.FullPath;

    // rm -r semantics: everything being removed must itself be writable and searchable.
    private void DemandRemovable(Node node)
    {
        Demand(node, Access.Write | Access.Execute, "remove the contents of");
        foreach (var child in node.Children.Values)
            DemandRemovable(child);
    }

    // True when the change is allowed and changes something.
    private bool CheckPermissionChange(PosixPermissions current, PosixPermissions requested)
    {
        if ((requested.Mode & ~PosixPermissions.Mask) != 0)
            throw new ArgumentException("A mode has nine bits (0777 at most).", nameof(requested));
        if (current == requested)
            return false;
        if (Identity.IsRoot)
            return true;

        DemandOwner(current, "change the permissions of");
        if (requested.Uid != current.Uid)
            throw new UnauthorizedAccessException("Permission denied: only root may change the owner.");
        if (requested.Gid != current.Gid && !Identity.InGroup(requested.Gid))
            throw new UnauthorizedAccessException("Permission denied: you may only hand an object to a group you belong to.");
        return true;
    }
}
