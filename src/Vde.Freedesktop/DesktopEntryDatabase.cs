namespace Vde.Freedesktop;

// Every .desktop file under the applications/ directories, user entries overriding system ones.
public sealed class DesktopEntryDatabase
{
    private readonly Dictionary<string, DesktopEntry> _byId = new(StringComparer.Ordinal);

    public IReadOnlyCollection<DesktopEntry> Entries => _byId.Values;
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public static DesktopEntryDatabase Load(XdgDirectories xdg, IReadOnlyList<string> locales)
    {
        var db = new DesktopEntryDatabase();
        var errors = new List<string>();
        var masked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dataDir in xdg.DataSearchPath)
        {
            var root = Path.Combine(dataDir, "applications");
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.desktop", SearchOption.AllDirectories))
            {
                // applications/kde/foo.desktop has the id "kde-foo.desktop"
                var id = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '-');
                if (db._byId.ContainsKey(id) || masked.Contains(id))
                    continue;

                try
                {
                    var entry = DesktopEntry.Load(file, id, locales);
                    if (entry.Hidden)
                        masked.Add(id);   // Hidden=true means "deleted": hide lower-priority copies too
                    else
                        db._byId[id] = entry;
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: {e.Message}");
                }
            }
        }

        db.Errors = errors;
        return db;
    }

    // Accepts the id with or without ".desktop".
    public DesktopEntry? Find(string id)
        => _byId.GetValueOrDefault(id.EndsWith(".desktop", StringComparison.Ordinal) ? id : id + ".desktop");
}
