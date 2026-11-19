using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// A read-only client for com.canonical.dbusmenu: enough of the protocol to show an item's menu when
// its tray icon is clicked and to send the click back. Submenus are read too; item icons beyond the
// name, radio groups, sections and scroll events are not — the rows are what menus are for.
public sealed class DBusMenuClient : IDisposable
{
    public const string Interface = "com.canonical.dbusmenu";

    private readonly DBusConnection _connection;

    public DBusMenuClient(DBusConnection connection, string service, string path)
    {
        _connection = connection;
        Service = service;
        Path = path;
    }

    public string Service { get; }
    public string Path { get; }

    public void Dispose() { }   // the connection outlives the menu

    // The whole reachable layout from the root, plus the app's revision.
    public Task<TrayMenuSnapshot> ReadAsync()
    {
        // The reply is (u revision, (i id, a{sv} properties, av children) layout) per the spec.
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, "GetLayout", "iias");
        w.WriteInt32(0);                     // from the root
        w.WriteInt32(-1);                    // every level
        w.WriteArray(Array.Empty<string>()); // every property
        return _connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            uint revision = r.ReadUInt32();
            r.AlignStruct();
            int id = r.ReadInt32();
            var props = r.ReadDictionaryOfStringToVariantValue();
            var children = r.ReadArrayOfVariantValue();
            return new TrayMenuSnapshot(revision,
                [FromProps(id, props, (children ?? []).SelectMany(ParseRow).ToList())]);
        }, null);
    }

    // The app may want to fill the menu before it is shown (dynamic menus); true means it changed.
    public Task<bool> AboutToShowAsync(int id)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, "AboutToShow", "i");
        w.WriteInt32(id);
        return _connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var v = m.GetBodyReader().ReadVariantValue();
            return v.Type == VariantValueType.Bool && v.GetBool();
        }, null);
    }

    // A click on a row. eventId is "clicked", or "toggled" for check rows.
    public Task SendEventAsync(int id, string eventId, object? data)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, "Event", "isvu");
        w.WriteInt32(id);
        w.WriteString(eventId);
        switch (data)
        {
            case bool b: w.WriteVariantBool(b); break;
            default: w.WriteVariantString(""); break;   // clicks carry no position
        }
        w.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return _connection.CallMethodAsync(w.CreateMessage());
    }

    // One (i id, a{sv} properties, av children) row: possibly nested, from a VariantValue (the
    // children read back either wrapped in their variant or bare).
    internal static IReadOnlyList<TrayMenuItem> ParseRow(VariantValue row)
    {
        if (row.Type == VariantValueType.Variant)
            return ParseRow(row.GetVariantValue());
        var fields = row.Fields();
        if (fields.Length < 3)
            return [];
        int id = fields[0].Int32();
        var props = fields[1].Dictionary();
        var children = fields[2].Array()
            .SelectMany(ParseRow)
            .ToList();
        return [FromProps(id, props, children)];
    }

    private static TrayMenuItem FromProps(int id, Dictionary<string, VariantValue> props, List<TrayMenuItem> children)
    {
        if (props.TryGetValue("type", out var t) && t.String() == "separator")
            return TrayMenuItem.Separator;
        // Labels carry mnemonics, decoded by DisplayLabel — keep the raw text here.
        string label = Text(props, "label");
        bool enabled = Bool(props, "enabled", true);
        bool visible = Bool(props, "visible", true);
        int toggle = props.TryGetValue("toggle-state", out var ts) ? ts.Int32() : -1;
        var icon = children.Count == 0 ? Text(props, "icon-name") : "";
        return new TrayMenuItem(id, label, enabled, visible, IsSeparator: false, toggle, icon, children);
    }

    private static bool Bool(Dictionary<string, VariantValue> props, string name, bool fallback)
        => !props.TryGetValue(name, out var v) ? fallback
         : v.Type == VariantValueType.Bool ? v.GetBool() : fallback;

    private static string Text(Dictionary<string, VariantValue> props, string name)
        => props.TryGetValue(name, out var v) ? v.String() : "";
}

// One reading of a menu: the app's revision and the rows from the root.
public sealed record TrayMenuSnapshot(uint Revision, IReadOnlyList<TrayMenuItem> Items)
{
    public IReadOnlyList<TrayMenuItem> VisibleItems => Items.Where(i => i.Visible).ToList();
}
