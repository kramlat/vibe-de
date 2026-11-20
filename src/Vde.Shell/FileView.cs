namespace Vde.Shell;

// How a folder's items are laid out. Details is the table; the icon views differ in picture size; Tiles is
// Windows XP's: a 48 pixel icon with the name and two lines of detail beside it (and, since Vista, a fullness bar
// under a drive).
public enum ViewMode { Details, Tiles, SmallIcons, MediumIcons, LargeIcons, ExtraLargeIcons }

// What a view groups by (Windows XP's "Show in groups", on every view since). None is the plain list.
public enum GroupBy { None, Name, Type, Size, Modified }

public static class ViewModes
{
    // The picture size of an icon view; 0 for the ones that are not icon grids.
    public static int IconSize(this ViewMode mode) => mode switch
    {
        ViewMode.SmallIcons => IconSizes.Medium, ViewMode.MediumIcons => IconSizes.Huge, ViewMode.LargeIcons => 96, ViewMode.ExtraLargeIcons => 160, _ => 0,
    };

    public static string Title(this ViewMode mode) => mode switch
    {
        ViewMode.Details => "Details", ViewMode.Tiles => "Tiles", ViewMode.SmallIcons => "Small icons", ViewMode.MediumIcons => "Medium icons",
        ViewMode.LargeIcons => "Large icons", _ => "Extra large icons",
    };

    public static string Title(this GroupBy by) => by switch
    {
        GroupBy.None => "None", GroupBy.Name => "Name", GroupBy.Type => "Type", GroupBy.Size => "Size", _ => "Date modified",
    };

    // What a folder starts as when the user has not chosen: This Computer is tiles grouped by type (folders, then
    // drives), the way Windows shows This PC; everything else is the plain details table.
    public static (ViewMode Mode, GroupBy Group) DefaultFor(string path)
        => string.Equals(path, "shell:Computer", StringComparison.OrdinalIgnoreCase) ? (ViewMode.Tiles, GroupBy.Type) : (ViewMode.MediumIcons, GroupBy.None);
}

public sealed record FileGroup(string Title, IReadOnlyList<FileEntry> Items);

// Splits entries into the groups a view shows under headings. A group's key and its place in the order are
// decided here so every view agrees.
public static class FileGrouping
{
    public static string Key(FileEntry entry, GroupBy by, DateTime now) => by switch
    {
        GroupBy.Name => NameKey(entry.Name),
        GroupBy.Type => entry.Group ?? (entry.IsDirectory ? "File folder" : entry.TypeText ?? (entry.Extension.Length > 0 ? entry.Extension.TrimStart('.').ToUpperInvariant() + " file" : "File")),
        GroupBy.Size => entry.IsDirectory ? "Folders" : SizeKey(entry.Size),
        GroupBy.Modified => ModifiedKey(entry.ModifiedUtc.ToLocalTime(), now),
        _ => "",
    };

    private static string NameKey(string name)
    {
        var c = name.TrimStart('.').FirstOrDefault();
        return c == default ? "#" : char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : char.IsDigit(c) ? "0 – 9" : "&";
    }

    private static readonly (long Limit, string Name)[] Sizes =
        [(0, "Empty"), (16L * 1024, "Tiny (0 – 16 KB)"), (1024L * 1024, "Small (16 KB – 1 MB)"), (128L * 1024 * 1024, "Medium (1 – 128 MB)"), (1024L * 1024 * 1024, "Large (128 MB – 1 GB)"), (long.MaxValue, "Huge (over 1 GB)")];

    private static string SizeKey(long size) => Sizes.First(s => size <= s.Limit).Name;

    private static readonly string[] ModifiedOrder = ["Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Last month", "Earlier this year", "A long time ago"];

    private static string ModifiedKey(DateTime time, DateTime now)
    {
        if (time == default) return "Unspecified";
        var day = now.Date;
        if (time.Date >= day) return ModifiedOrder[0];
        if (time.Date >= day.AddDays(-1)) return ModifiedOrder[1];
        var weekStart = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        if (time.Date >= weekStart) return ModifiedOrder[2];
        if (time.Date >= weekStart.AddDays(-7)) return ModifiedOrder[3];
        var monthStart = new DateTime(day.Year, day.Month, 1);
        if (time.Date >= monthStart) return ModifiedOrder[4];
        if (time.Date >= monthStart.AddMonths(-1)) return ModifiedOrder[5];
        if (time.Year == day.Year) return ModifiedOrder[6];
        return ModifiedOrder[7];
    }

    // The groups in the order they are shown. keepOrder (a shell folder) keeps the order the folder gave, groups by
    // their first appearance; otherwise groups follow their natural order (A..Z, small..huge, newest first) and the
    // items in a group the sort. GroupBy.None gives one untitled group.
    public static IReadOnlyList<FileGroup> Group(IReadOnlyList<FileEntry> entries, GroupBy by, SortKey sort, bool descending, bool keepOrder, DateTime? now = null)
    {
        if (by == GroupBy.None)
            return [new FileGroup("", keepOrder ? entries : FolderLister.Sort(entries, sort, descending))];
        var clock = now ?? DateTime.Now;
        var groups = new List<(string Key, List<FileEntry> Items)>();
        foreach (var entry in entries)
        {
            var key = Key(entry, by, clock);
            var group = groups.FirstOrDefault(g => g.Key == key);
            if (group.Items is null) { group = (key, new List<FileEntry>()); groups.Add(group); }
            group.Items.Add(entry);
        }
        if (!keepOrder)
        {
            int Rank(string key) => by switch
            {
                GroupBy.Size => key == "Folders" ? -1 : Array.FindIndex(Sizes, s => s.Name == key),
                GroupBy.Modified => key == "Unspecified" ? 99 : Array.IndexOf(ModifiedOrder, key),
                _ => 0,
            };
            groups.Sort((a, b) =>
            {
                int c = by is GroupBy.Size or GroupBy.Modified ? Rank(a.Key).CompareTo(Rank(b.Key)) : FolderLister.NaturalCompare(a.Key, b.Key);
                if (by == GroupBy.Type && a.Key != b.Key)
                    c = a.Key == "File folder" ? -1 : b.Key == "File folder" ? 1 : c;   // folders stay on top
                return descending && by != GroupBy.Type ? -c : c;
            });
        }
        return groups.Select(g => new FileGroup(g.Key, keepOrder ? g.Items : FolderLister.Sort(g.Items, sort, descending))).ToList();
    }
}
