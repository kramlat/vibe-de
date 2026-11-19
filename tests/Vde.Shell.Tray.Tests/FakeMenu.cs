using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray.Tests;

// A fake com.canonical.dbusmenu serving one fixed layout: a separator, one enabled row and one row
// with a child, plus the AboutToShow and Event members. Records clicks.
internal sealed class FakeMenu : IPathMethodHandler, IDisposable
{
    public const string DefaultPath = "/MenuBar";
    public const string Interface = "com.canonical.dbusmenu";

    private readonly DBusConnection _connection;

    private FakeMenu(DBusConnection connection, string service)
    {
        _connection = connection;
        Service = service;
    }

    public string Service { get; }
    public string Path { get; } = DefaultPath;
    public bool HandlesChildPaths => false;

    public List<int> Clicks { get; } = [];

    public static FakeMenu Start(FakeItem item)
    {
        // As in real apps, the menu is an object on the item's own connection.
        var menu = new FakeMenu(item.Connection, item.Service);
        item.Connection.AddMethodHandler(menu);
        return menu;
    }

    public void Dispose() { }   // the menu shares the item's connection

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        switch (request.MemberAsString)
        {
            case "GetLayout" when request.SignatureAsString == "iias":
                request.GetBodyReader();   // parentId, depth, propertyNames: the layout is fixed
                {
                    var writer = context.CreateReplyWriter("u(ia{sv}av)");
                    try
                    {
                        writer.WriteUInt32(2);                  // the app's revision
                        Row root = new(Id: 0, Children:
                        [
                            new Row(Separator: true),
                            new Row(Id: 1, Label: "_Click Me"),
                            new Row(Id: 2, Label: "S_u_b", Children: [new Row(Id: 3, Label: "Int__ernal")]),
                        ]);
                        // The root is a struct at reply top level: no signature blob on the wire
                        // (only variant/typed-array elements carry one).
                        writer.WriteStructureStart();
                        writer.WriteInt32(root.Id);
                        writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
                        var rootChildren = writer.WriteArrayStart(DBusType.Variant);
                        foreach (var child in root.Kids)
                            WriteItem(ref writer, child);
                        writer.WriteArrayEnd(rootChildren);
                        context.Reply(writer.CreateMessage());
                    }
                    finally { writer.Dispose(); }
                }
                break;
            case "AboutToShow" when request.SignatureAsString == "i":
                request.GetBodyReader().ReadInt32();
                using (var writer = context.CreateReplyWriter("v"))
                {
                    writer.WriteVariantBool(false);
                    context.Reply(writer.CreateMessage());
                }
                break;
            case "Event" when request.SignatureAsString == "isvu":
                var reader = request.GetBodyReader();
                var id = reader.ReadInt32();
                Assert.Equal("clicked", reader.ReadString());
                Clicks.Add(id);
                using (var writer = context.CreateReplyWriter("")) context.Reply(writer.CreateMessage());
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return default;
    }

    // Each item: (i id, a{sv} properties, av children).
    private sealed record Row(int Id = 0, string Label = "", bool Enabled = true, bool Separator = false, Row[]? Children = null)
    {
        public Row[] Kids => Children ?? [];
    }

    // A child of the layout, written as a variant: signature blob first, then the struct body.
    // The writer is a ref struct whose offset/span state lives in its fields: it must be passed
    // by ref, or the callee's writes advance a copy and never reach the message.
    private static void WriteItem(ref MessageWriter writer, Row row)
    {
        writer.WriteSignature("(ia{sv}av)");
        writer.WriteStructureStart();
        writer.WriteInt32(row.Id);
        var props = writer.WriteDictionaryStart();
        if (row.Separator)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("type");
            writer.WriteVariantString("separator");
        }
        else
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("label");
            writer.WriteVariantString(row.Label);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("enabled");
            writer.WriteVariantBool(row.Enabled);
        }
        writer.WriteDictionaryEnd(props);
        var children = writer.WriteArrayStart(DBusType.Variant);
        foreach (var child in row.Kids)
            WriteItem(ref writer, child);
        writer.WriteArrayEnd(children);
    }
}
