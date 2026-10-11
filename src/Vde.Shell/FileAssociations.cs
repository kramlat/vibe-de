using Vde.Freedesktop;

namespace Vde.Shell;

// Which application opens which MIME type, following the MIME Applications spec:
// mimeapps.list files (desktop-specific first, user before system), then the apps' own MimeType= lists.
public sealed class FileAssociations
{
    private readonly XdgDirectories _xdg;
    private readonly MimeDatabase _mime;
    private readonly DesktopEntryDatabase _apps;
    private readonly IReadOnlyList<string> _desktops;

    public FileAssociations(
        XdgDirectories xdg, MimeDatabase mime, DesktopEntryDatabase apps, IReadOnlyList<string> desktops)
    {
        _xdg = xdg;
        _mime = mime;
        _apps = apps;
        _desktops = desktops;
    }

    // The user's own list; SetDefault writes here.
    public string UserMimeAppsPath => Path.Combine(_xdg.ConfigHome, "mimeapps.list");

    public DesktopEntry? GetDefault(string mimeType) => GetHandlers(mimeType).FirstOrDefault();

    // The "Open with" list: the chosen default first, then other associated applications.
    // Handlers of parent types (a C source file is also plain text) come after those of the exact type.
    public IReadOnlyList<DesktopEntry> GetHandlers(string mimeType)
    {
        var lists = LoadLists();
        var result = new List<DesktopEntry>();

        void Add(string? id)
        {
            if (id is null || _apps.Find(id) is not { } app || result.Contains(app) || !IsUsable(app))
                return;
            result.Add(app);
        }

        foreach (var type in _mime.WithAncestors(mimeType))
        {
            var removed = lists.SelectMany(l => l.Removed(type)).ToHashSet(StringComparer.Ordinal);

            foreach (var list in lists)
                foreach (var id in list.Defaults(type))
                    Add(id);
            foreach (var list in lists)
                foreach (var id in list.Added(type))
                    if (!removed.Contains(id))
                        Add(id);

            foreach (var app in _apps.Entries
                         .Where(a => a.MimeTypes.Any(m => _mime.Canonical(m) == type) && !removed.Contains(a.Id))
                         .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                Add(app.Id);
        }
        return result;
    }

    public void SetDefault(string mimeType, DesktopEntry app)
    {
        mimeType = _mime.Canonical(mimeType);
        IniEditor.SetValue(UserMimeAppsPath, "Default Applications", mimeType, app.Id + ";");

        // The spec wants the chosen app to be an association too, so it stays in "Open with".
        var added = IniFile.Load(UserMimeAppsPath).GetList("Added Associations", mimeType);
        if (!added.Contains(app.Id))
            IniEditor.SetValue(UserMimeAppsPath, "Added Associations", mimeType,
                string.Concat(added.Append(app.Id).Select(i => i + ";")));
    }

    private static bool IsUsable(DesktopEntry app) => app.Type == "Application" && !app.Hidden && app.Exec is not null;

    private sealed class MimeAppsFile
    {
        private readonly IniFile _ini;
        public MimeAppsFile(IniFile ini) => _ini = ini;
        public IReadOnlyList<string> Defaults(string type) => _ini.GetList("Default Applications", type);
        public IReadOnlyList<string> Added(string type) => _ini.GetList("Added Associations", type);
        public IReadOnlyList<string> Removed(string type) => _ini.GetList("Removed Associations", type);
    }

    private List<MimeAppsFile> LoadLists()
    {
        var files = new List<string>();
        foreach (var configDir in _xdg.ConfigSearchPath)
        {
            foreach (var desktop in _desktops)
                files.Add(Path.Combine(configDir, desktop.ToLowerInvariant() + "-mimeapps.list"));
            files.Add(Path.Combine(configDir, "mimeapps.list"));
        }
        foreach (var dataDir in _xdg.DataSearchPath)
        {
            foreach (var desktop in _desktops)
                files.Add(Path.Combine(dataDir, "applications", desktop.ToLowerInvariant() + "-mimeapps.list"));
            files.Add(Path.Combine(dataDir, "applications", "mimeapps.list"));
        }

        return files.Where(File.Exists).Select(f => new MimeAppsFile(IniFile.Load(f))).ToList();
    }
}
