using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray.Tests;

// A fake org.kde.StatusNotifierItem: answers Properties.GetAll with the given properties and the
// org.kde.StatusNotifierItem methods (Activate and friends). Requests the given well-known name and
// tells the watcher about itself, as a real app would.
internal sealed class FakeItem : IPathMethodHandler, IDisposable
{
    public const string DefaultPath = "/StatusNotifierItem";
    public const string Interface = "org.kde.StatusNotifierItem";
    public const string Watcher = "org.kde.StatusNotifierWatcher";

    private readonly DBusConnection _connection;
    private (int X, int Y)? _activated;
    private (int X, int Y)? _secondary;

    public FakeItem(DBusConnection connection, string service)
    {
        _connection = connection;
        Service = service;
    }

    public string Service { get; }
    public DBusConnection Connection => _connection;
    public string Path { get; init; } = DefaultPath;
    // What it tells the watcher: its name (KDE style) or only its object path (libappindicator style).
    public string? RegisterAs { get; init; }
    public bool HandlesChildPaths => false;
    public (int X, int Y)? Activated => _activated;
    public (int X, int Y)? SecondaryActivated => _secondary;

    public sealed record Property(string Name, string? Text = null, (int W, int H, byte[] Data)[]? Pixmaps = null, string? Menu = null);

    public FakeItem.Property[] Properties { get; init; } = [];

    public static FakeItem Start(PrivateBus bus, string service, params FakeItem.Property[] properties)
    {
        var connection = bus.Connect();
        var item = new FakeItem(connection, service) { Properties = properties };
        connection.AddMethodHandler(item);
        connection.TryRequestNameAsync(service, RequestNameOptions.None).GetAwaiter().GetResult();
        item.RegisterToWatcher();
        return item;
    }

    // An item the way libappindicator and Ayatana make one: no well-known name, an object path of its own, and
    // the registration carries only that path (the watcher takes the sender as the bus name).
    public static FakeItem StartByPath(PrivateBus bus, string path, params FakeItem.Property[] properties)
    {
        var connection = bus.Connect();
        var item = new FakeItem(connection, "") { Properties = properties, Path = path, RegisterAs = path };
        connection.AddMethodHandler(item);
        item.RegisterToWatcher();
        return item;
    }

    // Tells the watcher about itself. A missing or contested watcher is tolerated — real apps hit
    // that while the tray is starting up.
    public void RegisterToWatcher()
    {
        try
        {
            using var w = _connection.GetMessageWriter();
            w.WriteMethodCallHeader(Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", "s");
            w.WriteString(RegisterAs ?? Service);
            _connection.CallMethodAsync(w.CreateMessage()).GetAwaiter().GetResult();
        }
        catch (DBusExceptionBase) { }
    }

    public void Dispose() => _connection.Dispose();

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case ("org.freedesktop.DBus.Properties", "GetAll") when request.SignatureAsString == "s":
                var iface = request.GetBodyReader().ReadString();
                using (var writer = context.CreateReplyWriter("a{sv}"))
                {
                    var dict = writer.WriteDictionaryStart();
                    if (iface == Interface)
                        foreach (var row in Properties)
                        {
                            writer.WriteDictionaryEntryStart();
                            writer.WriteString(row.Name);
                            if (row.Pixmaps is { } pixmaps)
                            {
                                // a(iiay): entries of (width, height, pixel bytes), inside a variant.
                                writer.WriteSignature("a(iiay)");
                                var entries = writer.WriteArrayStart(DBusType.Struct);
                                foreach (var pix in pixmaps)
                                {
                                    writer.WriteStructureStart();
                                    writer.WriteInt32(pix.W);
                                    writer.WriteInt32(pix.H);
                                    writer.WriteArray(pix.Data.AsSpan());
                                }
                                writer.WriteArrayEnd(entries);
                            }
                            else if (row.Menu is { } menuPath)
                            {
                                writer.WriteSignature("o");
                                writer.WriteObjectPath(new ObjectPath(menuPath));
                            }
                            else
                                writer.WriteVariantString(row.Text ?? "");
                        }
                    writer.WriteDictionaryEnd(dict);
                    context.Reply(writer.CreateMessage());
                }
                break;
            case (Interface, "Activate") when request.SignatureAsString == "ii":
                var a = request.GetBodyReader();
                _activated = (a.ReadInt32(), a.ReadInt32());
                using (var writer = context.CreateReplyWriter("")) context.Reply(writer.CreateMessage());
                break;
            case (Interface, "SecondaryActivate") when request.SignatureAsString == "ii":
                var s = request.GetBodyReader();
                _secondary = (s.ReadInt32(), s.ReadInt32());
                using (var writer = context.CreateReplyWriter("")) context.Reply(writer.CreateMessage());
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return default;
    }
}
