using Vde.Shell;
using Xunit;

namespace Vde.Shell.Tests;

public class FileGroupingTests
{
    private static FileEntry F(string name, long size = 1, string modified = "2026-11-01", bool dir = false)
        => new(name, "/x/" + name, dir, false, false, size, DateTime.Parse(modified).ToUniversalTime(), dir ? "" : Path.GetExtension(name).ToLowerInvariant());

    [Fact]
    public void Name_groups_are_letters_in_order_and_the_items_follow_the_sort()
    {
        var groups = FileGrouping.Group([F("beta.txt"), F("alpha.txt"), F("apple.txt"), F("9lives.txt")], GroupBy.Name, SortKey.Name, false, false);
        Assert.Equal(["0 – 9", "A", "B"], groups.Select(g => g.Title));
        Assert.Equal(["alpha.txt", "apple.txt"], groups[1].Items.Select(i => i.Name));
    }

    [Fact]
    public void Type_groups_put_folders_first_and_a_shell_folders_own_group_wins()
    {
        var groups = FileGrouping.Group([F("a.txt"), F("d", dir: true), F("b.png")], GroupBy.Type, SortKey.Name, false, false);
        Assert.Equal(["File folder", "PNG file", "TXT file"], groups.Select(g => g.Title));

        var computer = new[]
        {
            F("Docs", dir: true) with { Group = ComputerGroups.Folders }, F("Music", dir: true) with { Group = ComputerGroups.Folders },
            F("root", dir: true) with { Group = ComputerGroups.Drives },
        };
        var kept = FileGrouping.Group(computer, GroupBy.Type, SortKey.Name, false, keepOrder: true);
        Assert.Equal([ComputerGroups.Folders, ComputerGroups.Drives], kept.Select(g => g.Title));
        Assert.Equal(2, kept[0].Items.Count);
    }

    [Fact]
    public void Size_and_date_groups_use_the_buckets_in_natural_order()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);   // a Friday
        var groups = FileGrouping.Group([F("huge", 2L << 30), F("empty", 0), F("small", 5000)], GroupBy.Size, SortKey.Name, false, false, now);
        Assert.Equal(["Empty", "Tiny (0 – 16 KB)", "Huge (over 1 GB)"], groups.Select(g => g.Title));

        var dated = FileGrouping.Group([F("old", modified: "2020-01-01"), F("today", modified: "2026-11-20"), F("yesterday", modified: "2026-11-19"), F("week", modified: "2026-11-17")], GroupBy.Modified, SortKey.Name, false, false, now);
        Assert.Equal(["Today", "Yesterday", "Earlier this week", "A long time ago"], dated.Select(g => g.Title));
    }

    [Fact]
    public void No_grouping_is_one_untitled_group_and_computer_defaults_to_grouped_tiles_and_other_folders_to_medium_icons()
    {
        Assert.Single(FileGrouping.Group([F("a"), F("b")], GroupBy.None, SortKey.Name, false, false));
        Assert.Equal((ViewMode.Tiles, GroupBy.Type), ViewModes.DefaultFor("shell:Computer"));
        Assert.Equal((ViewMode.MediumIcons, GroupBy.None), ViewModes.DefaultFor("/home/deck"));
    }
}
