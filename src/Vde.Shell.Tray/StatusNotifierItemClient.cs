using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// One tray icon as it lives on the bus: reads org.kde.StatusNotifierItem's properties, follows its
// New* signals by re-reading, and calls its Activate / SecondaryActivate / ContextMenu.
public sealed class StatusNotifierItemClient : IDisposable
{
    public const string Interface = "org.kde.StatusNotifierItem";

    private readonly DBusConnection _connection;
    private readonly List<IDisposable> _watches = [];

    public string Service { get; }
    public string Path { get; }
    public TrayIcon Icon { get; private set; }
    public string MenuPath { get; private set; } = "";

    public event Action? Changed;

    // The item's connection left the bus: the host drops it.
    public event Action? Gone;

    private StatusNotifierItemClient(DBusConnection connection, string service, string path)
    {
        _connection = connection;
        Service = service;
        Path = path;
        Icon = new TrayIcon(service, path);
    }

    // registration: what the watcher holds, "bus-name" or "bus-name/object/path".
    public static Task<StatusNotifierItemClient?> OpenAsync(DBusConnection connection, string registration)
    {
        if (!IsValidRegistration(registration))
            return Task.FromResult<StatusNotifierItemClient?>(null);
        var (service, path) = SplitRegistration(registration);
        return OpenAsync(connection, service, path is null ? ObjectPathsFor(service) : [path]);
    }

    // The bus drops a connection that sends a message to a name or path that is not valid, so nothing from an
    // app is ever put into a message before it has been checked here.
    public static bool IsValidBusName(string name)
    {
        if (name.Length is 0 or > 255) return false;
        bool unique = name[0] == ':';
        var elements = (unique ? name[1..] : name).Split('.');
        if (elements.Length < 2 && !unique) return false;
        foreach (var element in elements)
        {
            if (element.Length == 0) return false;
            if (!unique && char.IsAsciiDigit(element[0])) return false;
            if (!element.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return false;
        }
        return true;
    }

    public static bool IsValidObjectPath(string path)
    {
        if (path == "/") return true;
        if (path.Length == 0 || path[0] != '/' || path.Length > 4096) return false;
        return path[1..].Split('/').All(e => e.Length > 0 && e.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
    }

    // What the watcher accepts as a registration: a valid bus name, optionally followed by a valid object path.
    public static bool IsValidRegistration(string registration)
    {
        var (service, path) = SplitRegistration(registration);
        return IsValidBusName(service) && (path is null || IsValidObjectPath(path));
    }

    // A bus name cannot contain '/', so the first one starts the object path.
    public static (string Service, string? Path) SplitRegistration(string registration)
    {
        int slash = registration.IndexOf('/');
        return slash < 0 ? (registration, null) : (registration[..slash], registration[slash..]);
    }

    // The paths an item may live at, in probing order: the one nearly everything uses, the KDE-planned path
    // derived from the name (dashes become slashes), then the other conventional one.
    public static string[] ObjectPathsFor(string service)
    {
        string derived = "/" + service.TrimStart('/').Replace('-', '/').Replace('.', '/');
        return ["/StatusNotifierItem", derived, "/org/kde/status_notifier_item"];
    }

    public static async Task<StatusNotifierItemClient?> OpenAsync(DBusConnection connection, string service, IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Where(IsValidObjectPath))
        {
            var client = new StatusNotifierItemClient(connection, service, path);
            try
            {
                var props = await ReadAllProps(connection, service, path);
                if (props.Count == 0) continue;
                client.Icon = ToTrayIcon(service, path, props);
                client.MenuPath = MenuPathFrom(props);
                await client.WatchAsync();
                return client;
            }
            catch (DBusExceptionBase)
            {
                // not there: the next path, then
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                // a malformed name or path from a strange client: not an item
            }
        }
        return null;
    }

    public void Dispose()
    {
        // may be reached from the host and from a bus signal at the same time
        IDisposable[] watches;
        lock (_watches)
        {
            watches = _watches.ToArray();
            _watches.Clear();
        }
        foreach (var w in watches)
            try { w.Dispose(); } catch (ObjectDisposedException) { }
    }

    private void Track(IDisposable watch)
    {
        lock (_watches) _watches.Add(watch);
    }

    // Left click, at the icon's screen position; -1 when unknown.
    public Task ActivateAsync(int x, int y) => CallAsync("Activate", "ii", x, y);

    // Right click when there is no menu.
    public Task SecondaryActivateAsync(int x, int y) => CallAsync("SecondaryActivate", "ii", x, y);

    public Task ContextMenuAsync(int x, int y) => CallAsync("ContextMenu", "ii", x, y);

    private Task CallAsync(string member, string signature, int x, int y)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, member, signature);
        w.WriteInt32(x);
        w.WriteInt32(y);
        return _connection.CallMethodAsync(w.CreateMessage());
    }

    private async Task WatchAsync()
    {
        // The item's own announcement signals: each means a fresh read of its properties.
        Track(await WatchSignalAsync("NewTitle", ReadIconAsync));
        Track(await WatchSignalAsync("NewIcon", ReadIconAsync));
        Track(await WatchSignalAsync("NewAttentionIcon", ReadIconAsync));
        Track(await WatchSignalAsync("NewToolTip", ReadIconAsync));
        Track(await WatchSignalAsync("NewStatus", ReadIconAsync));

        // The item disappears when its connection goes: the bus tells everyone.
        Track(await _connection.WatchSignalAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
            (Message m, object? _) => { var r = m.GetBodyReader(); return (r.ReadString(), r.ReadString(), r.ReadString()); },
            (Notification<(string, string, string)> n) =>
            {
                if (!n.HasValue || !n.IsCompletion) return;
                var v = n.Value;
                if (v.Item1 == Service && v.Item3 == "")
                    Gone?.Invoke();
            }, null, ObserverFlags.None, null));
    }

    private ValueTask<IDisposable> WatchSignalAsync(string member, Func<Task> onSignal)
        => _connection.WatchSignalAsync(Service, Path, Interface, member,
            (Message m, object? _) => 0,
            (Notification<int> _) => ReadAndPublish(onSignal), null, ObserverFlags.None, null);

    private async void ReadAndPublish(Func<Task> onSignal)
    {
        try
        {
            await onSignal();
        }
        catch (DBusExceptionBase)
        {
            // the item went away already
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ReadIconAsync()
    {
        var props = await ReadAllProps(_connection, Service, Path);
        if (props.Count == 0) return;
        Icon = ToTrayIcon(Service, Path, props);
        MenuPath = MenuPathFrom(props);
        Changed?.Invoke();
    }

    // ---- property reading ----

    internal static Task<Dictionary<string, VariantValue>> ReadAllProps(DBusConnection connection, string service, string path)
    {
        using var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader(service, path, "org.freedesktop.DBus.Properties", "GetAll", "s");
        w.WriteString(Interface);
        return connection.CallMethodAsync(w.CreateMessage(),
            (Message m, object? _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(), null);
    }

    internal static string? TextFrom(Dictionary<string, VariantValue> props, string name)
        => props.TryGetValue(name, out var v) && v.Type == VariantValueType.String ? v.GetString() : null;

    // The Menu property arrives as an object path, but an occasional toolkit sends a string.
    internal static string MenuPathFrom(Dictionary<string, VariantValue> props)
        => !props.TryGetValue("Menu", out var v) ? ""
         : v.Type == VariantValueType.ObjectPath ? v.Path().ToString()
         : v.Type == VariantValueType.String ? v.GetString()
         : "";

    // The item's pixmaps come as a(iiay): each entry (width, height, ARGB32 bytes); when there are
    // several sizes, the one closest to a 24px bar wins.
    internal static TrayPixmap? PixmapFrom(Dictionary<string, VariantValue> props, string name)
    {
        if (!props.TryGetValue(name, out var v) || v.Type != VariantValueType.Array)
            return null;
        (int w, int h, byte[] data)? best = null;
        foreach (var entry in v.GetArray<VariantValue>())
        {
            var fields = entry.Fields();
            if (fields.Length < 3) continue;
            int width = fields[0].Int32(), height = fields[1].Int32();
            var bytes = fields[2].Bytes();
            if (width <= 0 || height <= 0 || bytes.Length < width * height * 4) continue;
            int distance = Math.Abs(width - 24);
            if (best is null || distance < Math.Abs(best.Value.w - 24))
                best = (width, height, bytes);
        }
        return best is null ? null : new TrayPixmap(best.Value.w, best.Value.h, best.Value.data);
    }

    internal static TrayPixmap? Pixmap(VariantValue v)
    {
        var fields = v.Fields();
        if (fields.Length < 3) return null;
        int width = fields[0].Int32(), height = fields[1].Int32();
        var bytes = fields[2].Bytes();
        return width <= 0 || height <= 0 || bytes.Length < width * height * 4 ? null : new TrayPixmap(width, height, bytes);
    }

    internal static TrayTooltip? TooltipFrom(Dictionary<string, VariantValue> props)
    {
        if (!props.TryGetValue("ToolTip", out var v)) return null;
        var fields = v.Fields();
        if (fields.Length < 4) return null;
        string iconName = fields[0].String();
        TrayPixmap? icon = fields[1].Type == VariantValueType.Struct ? Pixmap(fields[1]) : null;
        string title = fields[2].String();
        string body = fields[3].String();
        if (title.Length == 0 && body.Length == 0 && iconName.Length == 0 && icon is null) return null;
        return new TrayTooltip(iconName, icon, title, body);
    }

    internal static TrayIcon ToTrayIcon(string service, string path, Dictionary<string, VariantValue> props)
        => new(service, path)
        {
            Id = TextFrom(props, "Id") ?? "",
            Title = TextFrom(props, "Title") ?? "",
            Category = TextFrom(props, "Category") ?? "",
            Status = TextFrom(props, "Status") ?? "Active",
            IconName = TextFrom(props, "IconName") ?? "",
            IconThemePath = TextFrom(props, "IconThemePath") ?? "",
            IconPixmap = PixmapFrom(props, "IconPixmap"),
            AttentionIconName = TextFrom(props, "AttentionIconName") ?? "",
            AttentionIconPixmap = PixmapFrom(props, "AttentionIconPixmap"),
            ToolTip = TooltipFrom(props),
        };
}
