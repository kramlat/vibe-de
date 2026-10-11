using System.Text;
using Tmds.DBus.Protocol;

namespace Vde.Shell.Notifications;

// org.freedesktop.Notifications (Desktop Notifications spec 1.2) on the session bus, in front of a
// NotificationCenter. D-Bus is only the way applications reach the shell; every rule lives in the center.
public sealed class NotificationsDBusServer : IPathMethodHandler, IDisposable
{
    public const string ServiceName = "org.freedesktop.Notifications";
    public const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";

    private static readonly string[] Capabilities = ["actions", "body", "body-markup", "body-hyperlinks", "icon-static", "persistence"];

    private readonly NotificationCenter _center;
    private readonly DBusConnection _connection;
    private readonly string _version;
    private readonly Dictionary<uint, string> _owners = new();   // notification id -> the bus name that sent it
    private readonly object _gate = new();

    private NotificationsDBusServer(NotificationCenter center, DBusConnection connection, string version)
    {
        _center = center;
        _connection = connection;
        _version = version;
        _center.Closed += OnClosed;
        _center.ActionInvoked += OnActionInvoked;
    }

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    // Connects to the bus at address (DBusAddress.Session for the real one), serves the interface and takes the
    // well-known name. Fails if another notification daemon already owns it: two must never fight over it.
    public static async Task<NotificationsDBusServer> StartAsync(NotificationCenter center, string address, string version = "0.1")
    {
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        await connection.ConnectAsync();
        var server = new NotificationsDBusServer(center, connection, version);
        connection.AddMethodHandler(server);

        bool owned = await connection.TryRequestNameAsync(ServiceName, RequestNameOptions.None);
        if (!owned)
        {
            server.Dispose();
            throw new InvalidOperationException($"Another notification daemon already owns {ServiceName}.");
        }
        return server;
    }

    public void Dispose()
    {
        _center.Closed -= OnClosed;
        _center.ActionInvoked -= OnActionInvoked;
        _connection.Dispose();
    }

    // ---- method calls ----

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;

        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes(IntrospectionXml) }, new List<string>());
            return default;
        }

        if (request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        try
        {
            switch (request.MemberAsString)
            {
                case "GetCapabilities" when request.SignatureAsString is null or "":
                    using (var writer = context.CreateReplyWriter("as"))
                    {
                        writer.WriteArray(Capabilities);
                        context.Reply(writer.CreateMessage());
                    }
                    break;

                case "GetServerInformation" when request.SignatureAsString is null or "":
                    using (var writer = context.CreateReplyWriter("ssss"))
                    {
                        writer.WriteString("VDE");
                        writer.WriteString("Vibe Desktop Environment");
                        writer.WriteString(_version);
                        writer.WriteString("1.2");
                        context.Reply(writer.CreateMessage());
                    }
                    break;

                case "Notify" when request.SignatureAsString == "susssasa{sv}i":
                    Notify(context);
                    break;

                case "CloseNotification" when request.SignatureAsString == "u":
                    Close(context);
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
        return default;
    }

    private void Notify(MethodContext context)
    {
        var sender = context.Request.SenderAsString ?? string.Empty;
        var reader = context.Request.GetBodyReader();
        var appName = reader.ReadString();
        var replacesId = reader.ReadUInt32();
        var appIcon = reader.ReadString();
        var summary = reader.ReadString();
        var body = reader.ReadString();
        var actionStrings = reader.ReadArrayOfString();
        var hints = reader.ReadDictionaryOfStringToVariantValue();
        var timeout = reader.ReadInt32();

        // Only the sender of a notification may replace it: asking to replace someone else's makes a new one.
        lock (_gate)
            if (replacesId != 0 && (!_owners.TryGetValue(replacesId, out var owner) || owner != sender))
                replacesId = 0;

        var actions = new List<NotificationAction>();
        for (int i = 0; i + 1 < actionStrings.Length; i += 2)
            actions.Add(new NotificationAction(actionStrings[i], actionStrings[i + 1]));

        var request = new NotificationRequest(
            appName, summary, replacesId, appIcon, body, actions, timeout,
            Urgency: UrgencyFrom(hints),
            Resident: Flag(hints, "resident"),
            Transient: Flag(hints, "transient"),
            Category: Text(hints, "category"),
            DesktopEntry: Text(hints, "desktop-entry"),
            ImagePath: Text(hints, "image-path") ?? Text(hints, "image_path"));

        uint id;
        lock (_gate)
        {
            id = _center.Notify(request);   // raises Added/Updated, which UI listens to
            _owners[id] = sender;
        }

        using var writer = context.CreateReplyWriter("u");
        writer.WriteUInt32(id);
        context.Reply(writer.CreateMessage());
    }

    private void Close(MethodContext context)
    {
        var id = context.Request.GetBodyReader().ReadUInt32();
        var sender = context.Request.SenderAsString ?? string.Empty;

        lock (_gate)
            if (_owners.TryGetValue(id, out var owner) && owner != sender)
            {
                context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Only the application that sent a notification may close it.");
                return;
            }

        _center.Close(id, CloseReason.ClosedByCall);   // an unknown id is not an error, as in the spec
        using var writer = context.CreateReplyWriter("");
        context.Reply(writer.CreateMessage());
    }

    // ---- signals ----

    private void OnClosed(object? sender, NotificationClosedEventArgs e)
    {
        lock (_gate)
            _owners.Remove(e.Id);
        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, Interface, "NotificationClosed", "uu");
        writer.WriteUInt32(e.Id);
        writer.WriteUInt32((uint)e.Reason);
        _connection.TrySendMessage(writer.CreateMessage());
    }

    private void OnActionInvoked(object? sender, ActionInvokedEventArgs e)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, Interface, "ActionInvoked", "us");
        writer.WriteUInt32(e.Id);
        writer.WriteString(e.Key);
        _connection.TrySendMessage(writer.CreateMessage());
    }

    // ---- hints ----

    // Applications send the urgency as a byte, but plenty send an int or a uint.
    private static Urgency UrgencyFrom(Dictionary<string, VariantValue> hints)
    {
        if (!hints.TryGetValue("urgency", out var value))
            return Urgency.Normal;
        long level = value.Type switch
        {
            VariantValueType.Byte => value.GetByte(),
            VariantValueType.Int16 => value.GetInt16(),
            VariantValueType.UInt16 => value.GetUInt16(),
            VariantValueType.Int32 => value.GetInt32(),
            VariantValueType.UInt32 => value.GetUInt32(),
            VariantValueType.Int64 => value.GetInt64(),
            VariantValueType.UInt64 => (long)Math.Min(value.GetUInt64(), 100),
            _ => 1,
        };
        return level switch { <= 0 => Urgency.Low, 1 => Urgency.Normal, _ => Urgency.Critical };
    }

    private static bool Flag(Dictionary<string, VariantValue> hints, string name)
        => hints.TryGetValue(name, out var value) && value.Type == VariantValueType.Bool && value.GetBool();

    private static string? Text(Dictionary<string, VariantValue> hints, string name)
        => hints.TryGetValue(name, out var value) && value.Type == VariantValueType.String ? value.GetString() : null;

    private const string IntrospectionXml = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.freedesktop.Notifications">
            <method name="GetCapabilities"><arg direction="out" name="capabilities" type="as"/></method>
            <method name="Notify">
              <arg direction="in" name="app_name" type="s"/>
              <arg direction="in" name="replaces_id" type="u"/>
              <arg direction="in" name="app_icon" type="s"/>
              <arg direction="in" name="summary" type="s"/>
              <arg direction="in" name="body" type="s"/>
              <arg direction="in" name="actions" type="as"/>
              <arg direction="in" name="hints" type="a{sv}"/>
              <arg direction="in" name="expire_timeout" type="i"/>
              <arg direction="out" name="id" type="u"/>
            </method>
            <method name="CloseNotification"><arg direction="in" name="id" type="u"/></method>
            <method name="GetServerInformation">
              <arg direction="out" name="name" type="s"/>
              <arg direction="out" name="vendor" type="s"/>
              <arg direction="out" name="version" type="s"/>
              <arg direction="out" name="spec_version" type="s"/>
            </method>
            <signal name="NotificationClosed"><arg name="id" type="u"/><arg name="reason" type="u"/></signal>
            <signal name="ActionInvoked"><arg name="id" type="u"/><arg name="action_key" type="s"/></signal>
          </interface>
        </node>
        """;
}
