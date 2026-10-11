namespace Vde.Freedesktop;

public sealed record DesktopAction(string Id, string Name, string? Icon, string? Exec);

// A parsed .desktop file (Desktop Entry spec 1.5).
public sealed class DesktopEntry
{
    private const string Group = "Desktop Entry";

    public string Id { get; init; } = string.Empty;          // "org.kde.dolphin.desktop"
    public string? FilePath { get; init; }
    public string Type { get; init; } = string.Empty;        // Application, Link, Directory
    public string Name { get; init; } = string.Empty;
    public string? GenericName { get; init; }
    public string? Comment { get; init; }
    public string? Icon { get; init; }
    public string? Exec { get; init; }
    public string? TryExec { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool Terminal { get; init; }
    public bool NoDisplay { get; init; }
    public bool Hidden { get; init; }
    public string? StartupWmClass { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
    public IReadOnlyList<string> OnlyShowIn { get; init; } = [];
    public IReadOnlyList<string> NotShowIn { get; init; } = [];
    public IReadOnlyList<DesktopAction> Actions { get; init; } = [];

    public static DesktopEntry Load(string path, string id, IReadOnlyList<string> locales)
    {
        var entry = Parse(File.ReadAllText(path), id, locales);
        return entry.FilePath is null ? CopyWithPath(entry, path) : entry;
    }

    public static DesktopEntry Parse(string text, string id, IReadOnlyList<string> locales)
    {
        var ini = IniFile.Parse(text);
        if (!ini.HasGroup(Group))
            throw new InvalidDataException("Missing [Desktop Entry] group.");

        var actions = new List<DesktopAction>();
        foreach (var actionId in ini.GetList(Group, "Actions"))
        {
            var g = "Desktop Action " + actionId;
            if (ini.HasGroup(g))
                actions.Add(new DesktopAction(actionId, ini.GetLocalized(g, "Name", locales) ?? actionId,
                    ini.Get(g, "Icon"), ini.Get(g, "Exec")));
        }

        return new DesktopEntry
        {
            Id = id,
            Type = ini.Get(Group, "Type") ?? string.Empty,
            Name = ini.GetLocalized(Group, "Name", locales) ?? string.Empty,
            GenericName = ini.GetLocalized(Group, "GenericName", locales),
            Comment = ini.GetLocalized(Group, "Comment", locales),
            Icon = ini.GetLocalized(Group, "Icon", locales),
            Exec = ini.Get(Group, "Exec"),
            TryExec = ini.Get(Group, "TryExec"),
            WorkingDirectory = ini.Get(Group, "Path"),
            Terminal = ini.GetBool(Group, "Terminal"),
            NoDisplay = ini.GetBool(Group, "NoDisplay"),
            Hidden = ini.GetBool(Group, "Hidden"),
            StartupWmClass = ini.Get(Group, "StartupWMClass"),
            Categories = ini.GetList(Group, "Categories"),
            MimeTypes = ini.GetList(Group, "MimeType"),
            OnlyShowIn = ini.GetList(Group, "OnlyShowIn"),
            NotShowIn = ini.GetList(Group, "NotShowIn"),
            Actions = actions,
        };
    }

    // XDG_CURRENT_DESKTOP entries, e.g. ["VDE"] or ["KDE"]. Empty OnlyShowIn means everywhere.
    public bool IsVisibleIn(IReadOnlyCollection<string> desktops)
    {
        if (OnlyShowIn.Count > 0 && !OnlyShowIn.Any(o => desktops.Contains(o, StringComparer.Ordinal)))
            return false;
        return !NotShowIn.Any(n => desktops.Contains(n, StringComparer.Ordinal));
    }

    // Shown in menus and launchers.
    public bool IsListed(IReadOnlyCollection<string> desktops)
        => Type == "Application" && !NoDisplay && !Hidden && IsVisibleIn(desktops);

    public IReadOnlyList<string> BuildCommand(IReadOnlyList<string> targets)
        => Exec is null ? [] : ExecLine.Expand(Exec, targets, Name, Icon, FilePath);

    private static DesktopEntry CopyWithPath(DesktopEntry e, string path) => new()
    {
        Id = e.Id, FilePath = path, Type = e.Type, Name = e.Name, GenericName = e.GenericName,
        Comment = e.Comment, Icon = e.Icon, Exec = e.Exec, TryExec = e.TryExec,
        WorkingDirectory = e.WorkingDirectory, Terminal = e.Terminal, NoDisplay = e.NoDisplay,
        Hidden = e.Hidden, StartupWmClass = e.StartupWmClass, Categories = e.Categories,
        MimeTypes = e.MimeTypes, OnlyShowIn = e.OnlyShowIn, NotShowIn = e.NotShowIn, Actions = e.Actions,
    };
}
