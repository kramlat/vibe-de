using Vde.Freedesktop;

namespace Vde.Shell;

// "How do you want to open this?": the applications that claim the file's type first, then every other
// application, narrowed by a search box. Choosing "always use this app" makes it the default for the type.
public sealed class OpenWithModel(FileAssociations associations, DesktopEntryDatabase apps, IReadOnlyList<string> desktops, string mimeType)
{
    private string _filter = "";

    public string MimeType => mimeType;

    public string Filter
    {
        get => _filter;
        set => _filter = value.Trim();
    }

    // Applications that say they handle this type (the user's choices and the system's), in the order the shell would offer them.
    public IReadOnlyList<DesktopEntry> Recommended => Filtered(associations.GetHandlers(mimeType));

    // Everything else that can be started: not already recommended, and not a settings program.
    public IReadOnlyList<DesktopEntry> Others
    {
        get
        {
            var recommended = associations.GetHandlers(mimeType).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            return Filtered(apps.Entries
                .Where(a => a.IsListed(desktops) && !a.Categories.Contains("Settings") && !recommended.Contains(a.Id))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
        }
    }

    public DesktopEntry? Selected { get; set; }
    public bool Always { get; set; }
    public bool CanOpen => Selected is not null;

    // Records the choice (as the type's default when "always" is on) and returns the application to open with.
    public DesktopEntry Commit()
    {
        var app = Selected ?? throw new InvalidOperationException("No application was chosen.");
        if (Always)
            associations.SetDefault(mimeType, app);
        return app;
    }

    private IReadOnlyList<DesktopEntry> Filtered(IEnumerable<DesktopEntry> list)
        => _filter.Length == 0
            ? list.ToList()
            : list.Where(a => a.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (a.GenericName ?? "").Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (a.Comment ?? "").Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
}
