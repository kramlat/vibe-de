using Vde.Freedesktop;

namespace VdeSession;

// Programs the user and the system want in every session: .desktop files in autostart/ of the config directories
// (the user's own first, and one with the same name overrides the system's, which is how the user switches one off).
public static class Autostart
{
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Command)> Find(XdgDirectories xdg, IReadOnlyCollection<string> desktops, Func<string, bool>? exists = null)
    {
        exists ??= name => ProgramFinder.Default.Find(name) is not null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, IReadOnlyList<string>)>();
        var dirs = new[] { xdg.ConfigHome }.Concat(xdg.ConfigDirs);
        foreach (var dir in dirs)
        {
            var folder = Path.Combine(dir, "autostart");
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.desktop").OrderBy(f => f, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(file);
                if (!seen.Add(id)) continue;   // a user's file of the same name already decided
                DesktopEntry entry;
                try { entry = DesktopEntry.Load(file, id, []); }
                catch (Exception e) when (e is IOException or InvalidDataException or FormatException) { continue; }
                if (entry.Type != "Application" || entry.Hidden || !entry.IsVisibleIn(desktops)) continue;
                if (entry.TryExec is { Length: > 0 } tryExec && !exists(tryExec)) continue;
                var command = entry.BuildCommand([]);
                if (command.Count > 0)
                    result.Add((entry.Name.Length > 0 ? entry.Name : id, command));
            }
        }
        return result;
    }
}
