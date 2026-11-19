using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// The shell side of the tray. Owns org.kde.StatusNotifierWatcher on the session bus, adopts every
// StatusNotifierItem that registers (including ones that registered before we stood up), and keeps
// the icon list current. The UI subscribes to Changed and re-renders from Icons.
public sealed class TrayHost : IDisposable
{
    private readonly DBusConnection _connection;
    private readonly StatusNotifierWatcherServer _watcher;
    private readonly Dictionary<string, StatusNotifierItemClient> _items = [];
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
    public static async Task<TrayHost?> StartAsync(string? address = null)
    {
        try
        {
            var watcher = await StatusNotifierWatcherServer.StartAsync(address ?? DBusAddress.Session!);
            var host = new TrayHost(watcher.Connection, watcher);
            await host.AdoptRegisteredItemsAsync();
            return host;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (DBusExceptionBase)
        {
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
            return _items.TryGetValue(icon.Service, out var item) ? item : null;
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
        foreach (var item in _items.Values)
            item.Dispose();
        _items.Clear();
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
                    if (newOwner.Length != 0 || !name.StartsWith("org.kde.StatusNotifierItem", StringComparison.Ordinal)) return;
                    Drop(name);
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
        StatusNotifierItemClient? item;
        lock (_gate)
            if (!_items.TryGetValue(service, out item)) return;
            else _items.Remove(service);
        item.Dispose();
        _watcher.UnregisterItem(service);
        Changed?.Invoke();
    }

    private async void OnItemRegistered(object? sender, string service)
    {
        // The app may still be exporting its item object; a few gentle rounds first.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var client = await StatusNotifierItemClient.OpenAsync(_connection, service);
            if (client is not null)
            {
                Adopt(client, announce: false);
                return;
            }
            await Task.Delay(250);
        }
        // Never appeared: drop it again so watchers downstream do not wait on a ghost.
        _watcher.UnregisterItem(service);
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

    private void Adopt(StatusNotifierItemClient client, bool announce)
    {
        client.Changed += () => Changed?.Invoke();
        client.Gone += () =>
        {
            client.Dispose();
            lock (_gate)
                _items.Remove(client.Service);
            _watcher.UnregisterItem(client.Service);
            Changed?.Invoke();
        };
        lock (_gate)
        {
            if (_items.Remove(client.Service)) { }
            _items[client.Service] = client;
        }
        Changed?.Invoke();
    }
}
