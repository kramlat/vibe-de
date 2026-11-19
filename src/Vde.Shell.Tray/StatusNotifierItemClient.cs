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

    public static Task<StatusNotifierItemClient?> OpenAsync(DBusConnection connection, string service)
        => OpenAsync(connection, service, ObjectPathsFor(service));

    // The paths an item may live at, in probing order: the KDE-planned path derived from the name
    // (dashes become slashes), then the two conventional ones.
    public static string[] ObjectPathsFor(string service)
    {
        string derived = "/" + service.TrimStart('/').Replace('-', '/').Replace('.', '/');
        return [derived, "/StatusNotifierItem", "/org/kde/status_notifier_item"];
    }

    public static async Task<StatusNotifierItemClient?> OpenAsync(DBusConnection connection, string service, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
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
            catch (InvalidOperationException)
            {
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var w in _watches)
            try { w.Dispose(); } catch (ObjectDisposedException) { }
        _watches.Clear();
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
        _watches.Add(await WatchSignalAsync("NewTitle", ReadIconAsync));
        _watches.Add(await WatchSignalAsync("NewIcon", ReadIconAsync));
        _watches.Add(await WatchSignalAsync("NewAttentionIcon", ReadIconAsync));
        _watches.Add(await WatchSignalAsync("NewToolTip", ReadIconAsync));
        _watches.Add(await WatchSignalAsync("NewStatus", ReadIconAsync));

        // The item disappears when its connection goes: the bus tells everyone.
        _watches.Add(await _connection.WatchSignalAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
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
            IconPixmap = PixmapFrom(props, "IconPixmap"),
            AttentionIconName = TextFrom(props, "AttentionIconName") ?? "",
            AttentionIconPixmap = PixmapFrom(props, "AttentionIconPixmap"),
            ToolTip = TooltipFrom(props),
        };
}
