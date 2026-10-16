using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

public enum ItemSource { BuiltIn, Registry, DesktopEntry }

// One thing the Control Panel can open: a built-in settings page, a registered applet, or a settings
// program from the system (a .desktop file such as KDE's modules).
public sealed record ControlPanelItem(
    string Id, string Name, string Description, string Icon, string Category,
    IReadOnlyList<string> Keywords, ItemSource Source)
{
    public string? PageId { get; init; }         // BuiltIn: the SettingsPage to show
    public string? ComponentClsid { get; init; } // Registry: a component (RegSvrVDE) that draws the page
    public string? Command { get; init; }        // Registry: a program to run
    public DesktopEntry? Entry { get; init; }    // DesktopEntry: launch it
}

public static class ControlPanelCategories
{
    public const string Appearance = "Appearance", System = "System", Devices = "Devices", Network = "Network",
        Privacy = "Privacy", Accessibility = "Accessibility", Apps = "Apps", Other = "Other";

    public static readonly string[] All = [Appearance, System, Devices, Network, Apps, Privacy, Accessibility, Other];

    // freedesktop menu categories -> ours.
    public static string FromDesktop(IReadOnlyList<string> categories)
    {
        foreach (var c in categories)
            switch (c)
            {
                case "DesktopSettings": return Appearance;
                case "HardwareSettings": case "Printing": return Devices;
                case "NetworkSettings": return Network;
                case "Accessibility": return Accessibility;
                case "Security": return Privacy;
            }
        return categories.Contains("System") ? System : Other;
    }
}

// Collects the items from every source, newest information winning: registry items of the user override the
// system's, and a registry item overrides a built-in with the same id.
public sealed class ControlPanelCatalog
{
    public const string RegistryKey = @"Software\Vde\ControlPanel\Items";

    private readonly List<ControlPanelItem> _items = new();

    public IReadOnlyList<ControlPanelItem> Items => _items;

    public static ControlPanelCatalog Load(IHive? system, IHive? user, DesktopEntryDatabase? apps, IReadOnlyList<string> desktops)
    {
        var catalog = new ControlPanelCatalog();
        foreach (var item in BuiltInPages.Items)
            catalog.Add(item);

        foreach (var hive in new[] { system, user })   // user last, so it overrides
            if (hive is not null && hive.KeyExists(RegistryKey))
                foreach (var id in hive.GetSubKeyNames(RegistryKey))
                    if (ReadRegistryItem(hive, id) is { } item)
                        catalog.Add(item);

        foreach (var app in apps?.Entries.Where(e => e.Categories.Contains("Settings") && e.IsListed(desktops)) ?? [])
            catalog.Add(new ControlPanelItem(
                "desktop:" + app.Id, app.Name, app.Comment ?? app.GenericName ?? string.Empty, app.Icon ?? "preferences-system",
                ControlPanelCategories.FromDesktop(app.Categories), [], ItemSource.DesktopEntry) { Entry = app });
        return catalog;
    }

    private void Add(ControlPanelItem item)
    {
        _items.RemoveAll(i => string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        _items.Add(item);
    }

    private static ControlPanelItem? ReadRegistryItem(IHive hive, string id)
    {
        var key = RegistryKey + "\\" + id;
        string? Text(string name) => hive.GetValue(key, name) is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;

        var name = Text("Name");
        var component = Text("Component");
        var command = Text("Command");
        if (name is null || (component is null && command is null))
            return null;   // an entry that cannot be opened or labelled is ignored, not shown broken

        var keywords = hive.GetValue(key, "Keywords") is { Kind: RegistryValueKind.MultiString } k ? (string[])k.Data! : [];
        var category = Text("Category");
        return new ControlPanelItem(id, name, Text("Description") ?? string.Empty, Text("Icon") ?? "preferences-system",
            category is not null && ControlPanelCategories.All.Contains(category, StringComparer.OrdinalIgnoreCase) ? category : ControlPanelCategories.Other,
            keywords, ItemSource.Registry)
        {
            ComponentClsid = ComponentClsidOrNull(component),
            Command = command,
        };
    }

    private static string? ComponentClsidOrNull(string? text) => Guid.TryParse(text, out var g) ? g.ToString("B").ToUpperInvariant() : null;

    public IReadOnlyList<ControlPanelItem> InCategory(string category)
        => _items.Where(i => i.Category == category).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // Best matches first. Whole-name and prefix matches beat word matches, which beat keyword and description hits.
    public IReadOnlyList<ControlPanelItem> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return [];

        return _items
            .Select(i => (item: i, score: Score(i, words)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.item).ToList();
    }

    private static int Score(ControlPanelItem item, string[] words)
    {
        int total = 0;
        foreach (var word in words)   // every word must match somewhere
        {
            int best = 0;
            if (item.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) best = 100;
            else if (item.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)) best = 80;
            else if (item.Name.Split(' ').Any(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase))) best = 60;
            else if (item.Keywords.Any(k => k.StartsWith(word, StringComparison.OrdinalIgnoreCase))) best = 40;
            else if (item.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) best = 30;
            else if (item.Keywords.Any(k => k.Contains(word, StringComparison.OrdinalIgnoreCase))) best = 20;
            else if (item.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) best = 10;
            if (best == 0) return 0;
            total += best;
        }
        return total;
    }
}
