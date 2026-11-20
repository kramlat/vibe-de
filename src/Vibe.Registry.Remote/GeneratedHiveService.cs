using System.Net.Sockets;

namespace Vibe.Registry.Remote;

// A hive that is not stored but made: filled from some other source of truth (the account files, the security
// modules), refilled when that changes, and served with fhived's protocol so each client is held to the
// permissions in the hive by its real uid and groups. PAMSAM (HKLM\SAM) and SECMAN (HKLM\Security) are built on it.
// Nothing written to such a hive is kept: the next refill replaces everything.
public sealed class GeneratedHiveService : IDisposable
{
    private readonly Action<Hive> _fill;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _refill, _periodic;

    // fill runs with the hive acting as root; periodic (optional) refills now and then for sources that cannot be watched (sysfs).
    public GeneratedHiveService(string name, Action<Hive> fill, Func<Socket, RegistryIdentity>? identityOf = null, TimeSpan? periodic = null)
    {
        _fill = fill;
        Hive = new Hive(name) { Identity = RegistryIdentity.Root };
        _refill = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        Refresh();
        Server = new HiveServer(Hive, identityOf);
        if (periodic is { } every)
            _periodic.Change(every, every);
    }

    public Hive Hive { get; }
    public HiveServer Server { get; }

    public void Refresh()
    {
        lock (Hive)
        {
            var before = Hive.Identity;
            Hive.Identity = RegistryIdentity.Root;
            try { _fill(Hive); }
            finally { Hive.Identity = before; }
        }
    }

    // Refills (after a short pause, so a burst of changes is one refill) when one of these files changes.
    public void Watch(IEnumerable<string> files)
    {
        var wanted = files.Select(Path.GetFullPath).ToHashSet();
        foreach (var dir in wanted.Select(f => Path.GetDirectoryName(f)!).Distinct().Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, e) => { if (wanted.Contains(Path.GetFullPath(e.FullPath))) _refill.Change(300, Timeout.Infinite); };
            watcher.Changed += changed; watcher.Created += changed; watcher.Renamed += (s, e) => changed(s, e);
            _watchers.Add(watcher);
        }
    }

    // Refills when anything in one of these folders changes (udev's device database, say).
    public void WatchDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories.Select(Path.GetFullPath).Distinct().Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, _) => _refill.Change(1000, Timeout.Infinite);
            watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed; watcher.Renamed += (s, e) => changed(s, e);
            _watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _refill.Dispose();
        _periodic.Dispose();
        Server.Dispose();
    }
}

// How generated hives mark who may read what.
public static class GeneratedHiveFill
{
    public static readonly PosixPermissions Browse = new(0, 0, 0x16D);       // r-xr-xr-x, root's
    public static readonly PosixPermissions ReadByAll = new(0, 0, 0x124);    // r--r--r--
    public static readonly PosixPermissions RootOnly = new(0, 0, 0x100);     // r--------

    // The key, and every key above it, browsable by everyone.
    public static void Key(Hive hive, string path)
    {
        hive.CreateKey(path);
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
            hive.SetKeyPermissions(string.Join('\\', parts.Take(i)), Browse);
    }

    public static void Public(Hive hive, string key, string name, RegistryValueKind kind, object data) => Set(hive, key, name, kind, data, ReadByAll);
    public static void Restricted(Hive hive, string key, string name, RegistryValueKind kind, object data) => Set(hive, key, name, kind, data, RootOnly);

    private static void Set(Hive hive, string key, string name, RegistryValueKind kind, object data, PosixPermissions permissions)
    {
        hive.SetValue(key, name, kind, data);
        hive.SetValuePermissions(key, name, permissions);
    }
}
