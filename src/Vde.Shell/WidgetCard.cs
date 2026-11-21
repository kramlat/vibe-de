using System.Text.Json;

namespace Vde.Shell;

// What an app-made widget sends the panel: a card described as data, not code. The panel draws it with its own controls, so a widget
// that lives in a sandbox (a Flatpak from Flathub) can offer one, nothing of it runs in the shell, and a card can only show what it
// says: text, pictures from its own files or the icon theme, a progress bar, a list and buttons. No scripts, no web content.
//
//   {"title":"Tides","icon":"weather-clear","rows":[
//      {"type":"text","text":"High 14:32","size":"large"},
//      {"type":"text","text":"Low 20:41","muted":true},
//      {"type":"progress","value":0.4,"label":"Tide"},
//      {"type":"list","items":[{"text":"Headline","subtitle":"Source","action":"open:1"}]},
//      {"type":"button","text":"Refresh","action":"refresh"}]}
public sealed record WidgetCard(string Title, string Icon, IReadOnlyList<WidgetRow> Rows)
{
    public const int MaxRows = 24, MaxItems = 12, MaxText = 400;

    public static WidgetCard? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var rows = new List<WidgetRow>();
            if (root.TryGetProperty("rows", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var row in array.EnumerateArray().Take(MaxRows))
                    if (ParseRow(row) is { } parsed) rows.Add(parsed);
            return new WidgetCard(Str(root, "title"), Str(root, "icon"), rows);
        }
        catch (JsonException) { return null; }
    }

    private static WidgetRow? ParseRow(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        switch (Str(e, "type"))
        {
            case "text": return new TextRow(Str(e, "text"), Str(e, "size") is "large" or "small" ? Str(e, "size") : "normal", Bool(e, "muted"));
            case "image": return new ImageRow(Str(e, "path"), e.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? Math.Clamp(hv, 8, 240) : 96);
            case "progress": return new ProgressRow(e.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0, Str(e, "label"));
            case "button": return new ButtonRow(Str(e, "text"), Str(e, "action"));
            case "list":
                var items = new List<ListItem>();
                if (e.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray().Take(MaxItems))
                        if (item.ValueKind == JsonValueKind.Object) items.Add(new ListItem(Str(item, "text"), Str(item, "subtitle"), Str(item, "action")));
                return new ListRow(items);
            default: return null;   // a kind this version does not know: skipped
        }
    }

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = v.GetString() ?? "";
        return s.Length > MaxText ? s[..MaxText] : s;
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

public abstract record WidgetRow;
public sealed record TextRow(string Text, string Size, bool Muted) : WidgetRow;
public sealed record ImageRow(string Path, int Height) : WidgetRow;   // an absolute file the widget can read, or an icon name
public sealed record ProgressRow(double Value, string Label) : WidgetRow;
public sealed record ButtonRow(string Text, string Action) : WidgetRow;
public sealed record ListItem(string Text, string Subtitle, string Action);
public sealed record ListRow(IReadOnlyList<ListItem> Items) : WidgetRow;
