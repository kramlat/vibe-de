using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// The shell side of the tray. Owns org.kde.StatusNotifierWatcher on the session bus, adopts every
// StatusNotifierItem that registers (including ones that registered before we stood up), and keeps
// the icon list current. The UI subscribes to Changed and re-renders from Icons.
public sealed class TrayHost : IDisposable
{
    private readonly DBusConnection _connection;
    private readonly StatusNotifierWatcherServer _watcher;
    // by what identifies one icon: the bus name and the object path (one app may export several icons)
    private readonly Dictionary<string, StatusNotifierItemClient> _items = [];
    // what the watcher knows each client as, to take it off its list again
    private readonly Dictionary<StatusNotifierItemClient, string> _registrations = [];
    private static string KeyOf(string service, string path) => service + path;
    private readonly object _gate = new();
    private readonly List<IDisposable> _watches = [];

    private TrayHost(DBusConnection connection, StatusNotifierWatcherServer watcher)
    {
        _connection = connection;
        _watcher = watcher;
        _watcher.ItemRegistered += OnItemRegistered;
        _ = WatchVacanciesAsync();
    }

    // Starts on the session bus. Returns null when the session has no bus, or when another tray
    // host already owns the watcher name — one host serves every icon; a second is a mistake.
    // Why it did not start is written to stderr (the session log): a missing tray is otherwise a mystery.
    public static async Task<TrayHost?> StartAsync(string? address = null)
    {
        address ??= DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            Console.Error.WriteLine("tray: no session bus (DBUS_SESSION_BUS_ADDRESS is not set): no tray icons");
            return null;
        }
        try
        {
            var watcher = await StatusNotifierWatcherServer.StartAsync(address);
            var host = new TrayHost(watcher.Connection, watcher);
            await host.AdoptRegisteredItemsAsync();
            Console.Error.WriteLine("tray: serving org.kde.StatusNotifierWatcher");
            return host;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine("tray: " + e.Message);   // another tray host owns the name
            return null;
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("tray: cannot use the session bus: " + e.Message);
            return null;
        }
    }

    public event Action? Changed;

    public IReadOnlyList<TrayIcon> Icons
    {
        get
        {
            lock (_gate)
                return _items.Values.Select(i => i.Icon).ToList();
        }
    }

    // The live client behind an icon (for clicks and menus).
    public StatusNotifierItemClient? Item(TrayIcon icon)
    {
        lock (_gate)
            return _items.TryGetValue(KeyOf(icon.Service, icon.Path), out var item) ? item : null;
    }

    // A menu the item exports, or null when it has none.
    public DBusMenuClient? Menu(TrayIcon icon)
    {
        var item = Item(icon);
        return item is { MenuPath.Length: > 0 } client
            ? new DBusMenuClient(_connection, client.Service, client.MenuPath)
            : null;
    }

    public void Dispose()
    {
        StatusNotifierItemClient[] items;
        lock (_gate)
        {
            items = _items.Values.ToArray();
            _items.Clear();
            _registrations.Clear();
        }
        foreach (var item in items)
            item.Dispose();
        foreach (var watch in _watches)
            try { watch.Dispose(); } catch (ObjectDisposedException) { }
        _watcher.Dispose();
    }

    // The bus announces a vanished name; an item's icon goes with it.
    private async Task WatchVacanciesAsync()
    {
        try
        {
            var watch = await _connection.WatchSignalAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
                (Message m, object? _) => { var r = m.GetBodyReader(); return (r.ReadString(), r.ReadString(), r.ReadString()); },
                (Notification<(string, string, string)> n) =>
                {
                    if (!n.HasValue) return;
                    var (name, _, newOwner) = n.Value;
                    if (newOwner.Length != 0) return;
                    Drop(name);   // the app's unique name or its well-known one: either way its icons go
                }, null, ObserverFlags.None, null);
            _watches.Add(watch);
        }
        catch (DBusExceptionBase)
        {
            // no bus, no watch
        }
    }

    private void Drop(string service)
    {
        List<(StatusNotifierItemClient Client, string Registration)> gone;
        lock (_gate)
        {
            gone = _items.Values.Where(c => c.Service == service).Select(c => (c, _registrations.GetValueOrDefault(c, service))).ToList();
            foreach (var (client, _) in gone)
            {
                _items.Remove(KeyOf(client.Service, client.Path));
                _registrations.Remove(client);
            }
        }
        if (gone.Count == 0) return;
        foreach (var (client, registration) in gone)
        {
            client.Dispose();
            _watcher.UnregisterItem(registration);
        }
        Changed?.Invoke();
    }

    private async void OnItemRegistered(object? sender, string registration)
    {
        // An async void method must not let anything out: it would take the whole process down.
        try
        {
            // The app may still be exporting its item object; a few gentle rounds first.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var client = await StatusNotifierItemClient.OpenAsync(_connection, registration);
                if (client is not null)
                {
                    Adopt(client, registration);
                    return;
                }
                await Task.Delay(250);
            }
            // Never appeared: drop it again so watchers downstream do not wait on a ghost.
            _watcher.UnregisterItem(registration);
        }
        catch (Exception e) when (e is DBusExceptionBase or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"tray: could not read item {registration}: {e.Message}");
            try { _watcher.UnregisterItem(registration); } catch (ObjectDisposedException) { }
        }
    }

    private Task<string[]> AdvertiseAsync()
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames", "");
        return _connection.CallMethodAsync(w.CreateMessage(),
            (Message m, object? _) => m.GetBodyReader().ReadArrayOfString(), null);
    }

    private async Task AdoptRegisteredItemsAsync()
    {
        string[] names;
        try
        {
            names = await AdvertiseAsync();
        }
        catch (DBusExceptionBase)
        {
            return;   // a bus that cannot list names: newcomers still register themselves
        }
        catch (InvalidOperationException)
        {
            return;
        }
        foreach (var name in names)
            if (name.StartsWith("org.kde.StatusNotifierItem", StringComparison.Ordinal))
                OnItemRegistered(this, name);
    }

    private void Adopt(StatusNotifierItemClient client, string registration)
    {
        client.Changed += () => Changed?.Invoke();
        client.Gone += () => Drop(client.Service);
        lock (_gate)
        {
            var key = KeyOf(client.Service, client.Path);
            if (_items.TryGetValue(key, out var old)) { old.Dispose(); _registrations.Remove(old); }
            _items[key] = client;
            _registrations[client] = registration;
        }
        Changed?.Invoke();
    }
}
