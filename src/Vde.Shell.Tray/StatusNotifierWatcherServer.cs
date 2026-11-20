using System.Text;
using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// org.kde.StatusNotifierWatcher on the session bus, at /StatusNotifierWatcher: the service tray
// icons register with. Mirrors NotificationsDBusServer's shape — a method handler plus the
// well-known name — but the protocol here is the StatusNotifierItem spec instead.
public sealed class StatusNotifierWatcherServer : IPathMethodHandler, IDisposable
{
    public const string ServiceName = "org.kde.StatusNotifierWatcher";
    public const string ObjectPath = "/StatusNotifierWatcher";
    private const string Interface = "org.kde.StatusNotifierWatcher";

    private readonly DBusConnection _connection;
    private readonly object _gate = new();

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    private StatusNotifierWatcherServer(DBusConnection connection)
    {
        _connection = connection;
    }

    // Connects to the bus at address (DBusAddress.Session for the real one) and takes the name.
    // Fails if another watcher already owns it: two must never fight over it, the icons go to one.
    public static async Task<StatusNotifierWatcherServer> StartAsync(string address)
    {
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        await connection.ConnectAsync();
        var server = new StatusNotifierWatcherServer(connection);
        connection.AddMethodHandler(server);

        bool owned = await connection.TryRequestNameAsync(ServiceName, RequestNameOptions.None);
        if (!owned)
        {
            connection.Dispose();
            throw new InvalidOperationException($"Another tray host already owns {ServiceName}.");
        }
        server.RegisterHost();   // announce as our own first host; failure does not stop the tray
        return server;
    }

    public void Dispose() => _connection.Dispose();

    public DBusConnection Connection => _connection;

    // Hosts announce themselves on the same interface; one is registered the moment we stand up.
    private async void RegisterHost()
    {
        try { await RegisterHostAsync(); }
        catch (DBusExceptionBase) { }   // a caller worrying about this wrote their own host
        catch (InvalidOperationException) { }
    }

    private Task RegisterHostAsync()
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(ServiceName, ObjectPath, Interface, "RegisterStatusNotifierHost", "s");
        w.WriteString(ServiceName);
        return _connection.CallMethodAsync(w.CreateMessage());
    }

    public event EventHandler<string>? ItemRegistered;
    public event EventHandler<string>? ItemUnregistered;

    // The service names we have accepted. Empty when everything went away.
    public IReadOnlyList<string> RegisteredItems
    {
        get { lock (_gate) return _items.ToList(); }
    }
    private readonly List<string> _items = [];

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;

        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes(IntrospectionXml) }, new List<string>());
            return default;
        }

        if (request.InterfaceAsString != Interface && request.InterfaceAsString != "org.freedesktop.DBus.Properties")
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        try
        {
            if (request.InterfaceAsString == "org.freedesktop.DBus.Properties")
            {
                HandleProperties(context);
                return default;
            }

            switch (request.MemberAsString)
            {
                case "RegisterStatusNotifierItem" when request.SignatureAsString is "s":
                    {
                        // An app registers either its bus name (KDE style: "org.kde.StatusNotifierItem-1234-1", the item at
                        // /StatusNotifierItem) or just the object path (libappindicator / Ayatana style: "/org/ayatana/...");
                        // in that case the item lives on the connection that sent the call, and the registration is
                        // "sender/path", which is what hosts read back from RegisteredStatusNotifierItems.
                        var service = request.GetBodyReader().ReadString();
                        RegisterItem(service.StartsWith('/') && request.SenderAsString is { Length: > 0 } sender ? sender + service : service);
                        using var writer = context.CreateReplyWriter("");
                        context.Reply(writer.CreateMessage());
                        break;
                    }
                case "RegisterStatusNotifierHost" when request.SignatureAsString is "s":
                    using (var writer = context.CreateReplyWriter(""))
                        context.Reply(writer.CreateMessage());
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }
        catch (Exception e) when (e is DBusReadException or InvalidDataException or ArgumentException)
        {
            context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", e.Message);
        }
        catch (Exception e)
        {
            // Whatever a client sends, an exception here must never escape: it would end the bus connection and the
            // tray with it. The caller is told, and everyone else carries on.
            context.ReplyError("org.freedesktop.DBus.Error.Failed", e.Message);
        }
        return default;
    }

    private void RegisterItem(string service)
    {
        if (!StatusNotifierItemClient.IsValidRegistration(service))
            throw new ArgumentException($"'{service}' is not a bus name or an object path");
        bool added;
        lock (_gate)
        {
            added = !_items.Contains(service);
            if (added)
                _items.Add(service);
        }
        if (added)
        {
            Emit("StatusNotifierItemRegistered", "s", service);
            ItemRegistered?.Invoke(this, service);
        }
    }

    // Called by the host when an item's connection vanished or its icons are gone.
    public void UnregisterItem(string service)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(service);
        }
        if (removed)
        {
            Emit("StatusNotifierItemUnregistered", "s", service);
            ItemUnregistered?.Invoke(this, service);
        }
    }

    private void HandleProperties(MethodContext context)
    {
        var request = context.Request;
        if (request.MemberAsString is "Get")
        {
            var reader = request.GetBodyReader();
            reader.ReadString();   // interface
            var property = reader.ReadString();
            switch (property)
            {
                case "RegisteredStatusNotifierItems":
                    // a variant is its type string followed by the value
                    using (var writer = context.CreateReplyWriter("v"))
                    {
                        writer.WriteSignature("as");
                        writer.WriteArray(RegisteredItems);
                        context.Reply(writer.CreateMessage());
                    }
                    return;
                case "IsStatusNotifierHostRegistered":
                    using (var writer = context.CreateReplyWriter("v"))
                    {
                        writer.WriteVariantBool(true);
                        context.Reply(writer.CreateMessage());
                    }
                    return;
                case "ProtocolVersion":
                    using (var writer = context.CreateReplyWriter("v"))
                    {
                        writer.WriteVariantInt32(0);
                        context.Reply(writer.CreateMessage());
                    }
                    return;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "No such property: " + property);
                    return;
            }
        }
        if (request.MemberAsString is "GetAll")
        {
            using var writer = context.CreateReplyWriter("a{sv}");
            var dict = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("RegisteredStatusNotifierItems");
            writer.WriteSignature("as");
            writer.WriteArray(RegisteredItems);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("IsStatusNotifierHostRegistered");
            writer.WriteVariantBool(true);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("ProtocolVersion");
            writer.WriteVariantInt32(0);
            writer.WriteDictionaryEnd(dict);
            context.Reply(writer.CreateMessage());
            return;
        }
        context.ReplyUnknownMethodError();
    }

    private void Emit(string member, string signature, string value)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, Interface, member, signature);
        writer.WriteString(value);
        _connection.TrySendMessage(writer.CreateMessage());
    }

    private const string IntrospectionXml = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.kde.StatusNotifierWatcher">
            <method name="RegisterStatusNotifierItem"><arg direction="in" name="service" type="s"/></method>
            <method name="RegisterStatusNotifierHost"><arg direction="in" name="service" type="s"/></method>
            <property name="RegisteredStatusNotifierItems" type="as" access="read"/>
            <property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
            <property name="ProtocolVersion" type="i" access="read"/>
            <signal name="StatusNotifierItemRegistered"><arg name="service" type="s"/></signal>
            <signal name="StatusNotifierItemUnregistered"><arg name="service" type="s"/></signal>
            <signal name="StatusNotifierHostRegistered"/>
          </interface>
        </node>
        """;
}
