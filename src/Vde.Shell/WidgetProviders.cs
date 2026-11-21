using Vde.Components;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record WidgetProviderInfo(ComponentRegistration Registration, bool IsAllowed)
{
    public string Clsid => Registration.Component.Clsid;
    public string Name => Registration.Component.Description ?? Registration.Component.ClassName;
    public bool MayLoad => IsAllowed && Registration.Trust.IsTrusted;   // the user allowed it AND the registration is trusted
}

// The community's widgets: components registered in ComponentCategories.Widget. A widget runs only when this user allowed it (a value
// in the user's own hive, which no installer can set for them) and its registration is trusted, exactly as for launcher search plugins.
// The panel's host remembers what is installed (Remember) so the Settings window, another process, can list them to be allowed.
public sealed class WidgetProviders(ClassesRoot classes, IHive userHive, RegistryIdentity? identity = null)
{
    public const string KeyPath = WidgetSettings.KeyPath + @"\Community";

    private readonly ComponentCatalog _catalog = new(classes, identity);

    public IReadOnlyList<WidgetProviderInfo> List()
        => _catalog.All().Where(r => r.Component.Categories?.Contains(ComponentCategories.Widget, StringComparer.OrdinalIgnoreCase) == true)
            .Select(r => new WidgetProviderInfo(r, IsAllowed(r.Component.Clsid))).ToList();

    // A component widget is named by its CLSID, an app widget by its desktop id; both are allowed the same way.
    public static string KeyOf(string id) => TrayPreferences.KeyFor(ComponentInfo.NormalizeGuid(id) ?? id);

    public bool IsAllowed(string id) => RegistryBool.Get(userHive, $@"{KeyPath}\{KeyOf(id)}", "Allowed") == true;

    public void Allow(string id, bool allowed) => RegistryBool.Set(userHive, $@"{KeyPath}\{KeyOf(id)}", "Allowed", allowed);

    // Notes the installed widgets, component and app ones, where the Settings window can read them.
    public void Remember(IEnumerable<(string Id, string Title)>? apps = null)
    {
        foreach (var (id, title) in List().Select(i => (i.Clsid, i.Name)).Concat(apps ?? []))
        {
            var path = $@"{KeyPath}\{KeyOf(id)}";
            if (userHive.GetValue(path, "Title") is not { Kind: RegistryValueKind.String } t || (string?)t.Data != title)
                userHive.SetValue(path, "Title", RegistryValueKind.String, title);
        }
    }

    // The widgets to show: allowed, trusted and able to load. One that fails to load is skipped; it cannot break the panel.
    public IReadOnlyList<(string Id, IWidgetProvider Provider)> Load(ComponentLoader loader)
    {
        var widgets = new List<(string, IWidgetProvider)>();
        foreach (var info in List().Where(i => i.Registration.Trust.IsTrusted && IsAllowed(i.Clsid)))
        {
            try { widgets.Add((info.Clsid, loader.Get<IWidgetProvider>(info.Registration))); }
            catch (ComponentLoadException) { }
        }
        return widgets;
    }

    // The installed widgets as switches for the settings page (off until the user allows each).
    public static IReadOnlyList<SettingDefinition> Definitions(IHive hive)
    {
        if (!hive.KeyExists(KeyPath)) return [];
        return hive.GetSubKeyNames(KeyPath)
            .Select(id => (Id: id, Title: hive.GetValue($@"{KeyPath}\{id}", "Title") is { Kind: RegistryValueKind.String } t ? (string?)t.Data ?? id : id))
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new SettingDefinition("widget-" + w.Id, w.Title, SettingKind.Toggle, $@"{KeyPath}\{w.Id}", "Allowed", "A community widget: it runs only while this is on.") { Default = false })
            .ToList();
    }
}
