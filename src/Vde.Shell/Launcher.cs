using Vde.Components;
using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record LauncherGroup(string Heading, IReadOnlyList<DesktopEntry> Apps);

public sealed record LauncherResult(string Kind, string Title, string Subtitle, string Icon, DesktopEntry? App = null, ControlPanelItem? Setting = null)
{
    public Vde.Components.ILauncherSearchProvider? Provider { get; init; }
    public Vde.Components.SearchHit? Hit { get; init; }
    public string? ProviderClsid { get; init; }
    public string? FilePath { get; init; }
}

// The start menu's data: pinned apps, recently used apps and the alphabetical list of everything, plus search.
// Everything here is local. There are no web results, no suggested or sponsored apps and nothing is reported
// anywhere: the lists are exactly what is installed and what the user pinned or ran. Search plugins can add
// results, but only ones the user authorized (LauncherProviders), and only for as long as that stands.
// Pinned and recent are kept in the registry (HKCU\Software\Vde\Launcher) so every shell window agrees.
public sealed class LauncherModel
{
    public const string KeyPath = @"Software\Vde\Launcher";
    public const int MaxRecent = 8;

    private readonly IHive _hive;
    private readonly DesktopEntryDatabase _apps;
    private readonly ControlPanelCatalog? _settings;
    private readonly IReadOnlyList<string> _desktops;

    public LauncherModel(IHive hive, DesktopEntryDatabase apps, IReadOnlyList<string> desktops, ControlPanelCatalog? settings = null)
    {
        _hive = hive;
        _apps = apps;
        _desktops = desktops;
        _settings = settings;
    }

    // Every application worth listing, by name.
    public IReadOnlyList<DesktopEntry> AllApps
        => _apps.Entries.Where(e => e.IsListed(_desktops) && !e.Categories.Contains("Settings"))   // settings programs belong to the Control Panel
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // "A", "B", ... with "#" for names that do not start with a letter, as the Start menu's list does.
    public IReadOnlyList<LauncherGroup> Alphabetical()
        => AllApps.GroupBy(e => Initial(e.Name)).OrderBy(g => g.Key == "#" ? "￿" : g.Key, StringComparer.Ordinal)
            .Select(g => new LauncherGroup(g.Key, g.ToList())).ToList();

    public IReadOnlyList<DesktopEntry> Pinned => Resolve(ReadList("Pinned"));

    // Newest first. An app that was uninstalled since simply drops out.
    public IReadOnlyList<DesktopEntry> Recent => Resolve(ReadList("Recent")).Take(MaxRecent).ToList();

    public void Pin(string appId)
    {
        var pinned = ReadList("Pinned");
        if (_apps.Find(appId) is { } app && !pinned.Contains(app.Id))
            WriteList("Pinned", pinned.Append(app.Id));
    }

    public void Unpin(string appId) => WriteList("Pinned", ReadList("Pinned").Where(i => i != appId));

    // Moves a pinned app to a new position (drag to reorder). Out-of-range positions go to the end.
    public void MovePinned(string appId, int index)
    {
        var pinned = ReadList("Pinned");
        if (!pinned.Remove(appId))
            return;
        pinned.Insert(Math.Clamp(index, 0, pinned.Count), appId);
        WriteList("Pinned", pinned);
    }

    // Call when an app is started from the launcher: it goes to the top of Recent.
    public void RecordLaunch(string appId)
    {
        if (_apps.Find(appId) is not { } app)
            return;
        var recent = ReadList("Recent").Where(i => i != app.Id).Prepend(app.Id).Take(MaxRecent * 2);   // keep a little more than shown
        WriteList("Recent", recent);
    }

    public void ClearRecent() => _hive.DeleteValue(KeyPath, "Recent");

    // Apps and settings that match, best first. Names starting with the text come before names containing it.
    private readonly List<(string clsid, ILauncherSearchProvider provider)> _plugins = new();
    public LauncherProviders? Authorizer { get; set; }
    public TimeSpan PluginTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    public const int MaxPluginHits = 5;

    // Registers a plugin the host has loaded. It is only ever asked anything while the user's authorization stands.
    public void AddPlugin(string clsid, ILauncherSearchProvider provider) => _plugins.Add((clsid, provider));

    // Local results first (instant), then the authorized plugins' hits, each labelled with its plugin.
    // A plugin that is slow, throws or is no longer authorized adds nothing and never breaks the search.
    public async Task<IReadOnlyList<LauncherResult>> SearchAsync(string text, CancellationToken cancel = default)
    {
        var results = Search(text).ToList();
        if (text.Trim().Length == 0)
            return results;

        var asks = _plugins.Where(p => Authorizer?.IsAuthorized(p.clsid) == true).Select(p => AskAsync(p.clsid, p.provider, text, cancel)).ToList();
        foreach (var hits in await Task.WhenAll(asks))
            results.AddRange(hits);
        return results;
    }

    private async Task<IReadOnlyList<LauncherResult>> AskAsync(string clsid, ILauncherSearchProvider provider, string text, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(PluginTimeout);
        try
        {
            var task = provider.SearchAsync(text, limit.Token);
            if (await Task.WhenAny(task, Task.Delay(PluginTimeout, cancel)) != task)
            {
                limit.Cancel();
                return [];   // too slow: do not wait for it, and ignore what it returns later
            }
            return (await task).Take(MaxPluginHits)
                .Select(h => new LauncherResult("Plugin", h.Title, string.IsNullOrEmpty(h.Subtitle) ? provider.Name : h.Subtitle + " · " + provider.Name,
                    string.IsNullOrEmpty(h.Icon) ? "system-search" : h.Icon) { Provider = provider, Hit = h, ProviderClsid = clsid }).ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];   // a plugin's bug is the plugin's problem
        }
    }

    // Files and folders under these roots (the user's own folders) whose name matches, or, with "in:" first,
    // whose text contains what follows: "in:invoice 2026" is like grep for "invoice 2026". Local only.
    public IReadOnlyList<string> FileRoots { get; set; } = [];

    public Task<IReadOnlyList<LauncherResult>> SearchFilesAsync(string text, CancellationToken cancel = default)
        => Task.Run<IReadOnlyList<LauncherResult>>(() =>
        {
            text = text.Trim();
            bool content = text.StartsWith("in:", StringComparison.OrdinalIgnoreCase);
            var hits = content
                ? FileSearch.ByContent(FileRoots, text[3..].Trim(), cancel: cancel)
                : FileSearch.ByName(FileRoots, text, cancel: cancel);
            return hits.Select(h => new LauncherResult(h.IsDirectory ? "Folder" : "File", h.Name,
                h.Line is { } line ? $"{h.Path}:{line}  {h.Snippet}" : h.Path, h.IsDirectory ? "folder" : "text-x-generic")
            { FilePath = h.Path }).ToList();
        }, cancel);

    // Runs a plugin hit, if the plugin is still authorized.
    public async Task<bool> ActivateAsync(LauncherResult result, CancellationToken cancel = default)
    {
        if (result.Provider is null || result.Hit is null || result.ProviderClsid is null || Authorizer?.IsAuthorized(result.ProviderClsid) != true)
            return false;
        await result.Provider.ActivateAsync(result.Hit, cancel);
        return true;
    }

    public IReadOnlyList<LauncherResult> Search(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return [];

        var results = new List<(int score, LauncherResult result)>();
        foreach (var app in AllApps)
        {
            int score = ScoreApp(app, text);
            if (score > 0)
                results.Add((score, new LauncherResult("App", app.Name, app.GenericName ?? "App", app.Icon ?? "application-x-executable", App: app)));
        }
        foreach (var item in _settings?.Search(text) ?? [])
            results.Add((15, new LauncherResult("Setting", item.Name, "Settings", item.Icon, Setting: item)));   // below real app-name matches

        return results.OrderByDescending(r => r.score).ThenBy(r => r.result.Title, StringComparer.CurrentCultureIgnoreCase).Select(r => r.result).ToList();
    }

    private static int ScoreApp(DesktopEntry app, string text)
    {
        if (app.Name.Equals(text, StringComparison.OrdinalIgnoreCase)) return 100;
        if (app.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 80;
        if (app.Name.Split(' ', '-').Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase))) return 60;
        if (app.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return 40;
        if ((app.GenericName ?? "").Contains(text, StringComparison.OrdinalIgnoreCase) || (app.Comment ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)) return 20;
        return 0;
    }

    private static string Initial(string name)
    {
        var first = name.TrimStart().FirstOrDefault();
        return char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : "#";
    }

    private IReadOnlyList<DesktopEntry> Resolve(IEnumerable<string> ids)
        => ids.Select(_apps.Find).Where(a => a is not null && a.IsListed(_desktops)).Select(a => a!).ToList();

    private List<string> ReadList(string name)
        => _hive.GetValue(KeyPath, name) is { Kind: RegistryValueKind.MultiString } v ? [.. (string[])v.Data!] : [];

    private void WriteList(string name, IEnumerable<string> ids)
    {
        var list = ids.ToArray();
        if (list.Length == 0)
            _hive.DeleteValue(KeyPath, name);
        else
            _hive.SetValue(KeyPath, name, RegistryValueKind.MultiString, list);
    }
}
