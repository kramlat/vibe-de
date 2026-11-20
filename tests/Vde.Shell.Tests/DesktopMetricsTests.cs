using Vde.Shell;

namespace Vde.Shell.Tests;

public class DesktopMetricsTests
{
    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 16)]
    [InlineData(20, 22)]
    [InlineData(24, 22)]
    [InlineData(28, 32)]
    [InlineData(40, 32)]
    [InlineData(41, 48)]
    [InlineData(80, 64)]
    [InlineData(81, 96)]
    [InlineData(300, 256)]
    [InlineData(1, 16)]
    public void Icon_sizes_move_to_the_nearest_size_themes_are_drawn_at(int asked, int snapped)
        => Assert.Equal(snapped, IconSizes.Snap(asked));

    [Fact]
    public void A_cell_is_the_icon_plus_room_for_two_lines_of_label_the_way_Plasma_sizes_it()
    {
        var m = new DesktopMetrics(IconSizes.Huge);          // 64 px icons, Plasma's default
        Assert.Equal(64 + 2 * 18 + 2 * 4, m.MinCellWidth);   // iconSize + 2 gridUnit + 2 smallSpacing
        Assert.Equal(64 + 18 * 2 + 4 * 3, m.MinCellHeight);  // iconSize + gridUnit * lines + 3 smallSpacing
        Assert.Equal(108, m.MinCellWidth);
        Assert.Equal(112, m.MinCellHeight);

        // a very small icon is still given room for a label of the chosen width (16 * (2 * width + 4))
        Assert.Equal(16 * 6, new DesktopMetrics(22).MinCellWidth);
        Assert.Equal(16 * 8, new DesktopMetrics(22, LabelWidth: 2).MinCellWidth);
    }

    [Fact]
    public void What_is_left_at_the_edge_of_the_screen_is_shared_out_in_whole_pixels()
    {
        // 1280 wide, cells 108: eleven columns use 1188, 92 are left, 8 each (floored)
        Assert.Equal(8, DesktopMetrics.ExtraSpacing(108, 1280));
        var m = new DesktopMetrics(IconSizes.Huge);
        Assert.Equal(116, m.CellWidth(1280));
        Assert.Equal(11 * 116, 1276);                         // the grid fills the screen to within a few pixels
        Assert.True(11 * m.CellWidth(1280) <= 1280);
        Assert.Equal(0, DesktopMetrics.ExtraSpacing(108, 50)); // not even one column fits: nothing to share
        Assert.Equal(0, DesktopMetrics.ExtraSpacing(100, 300)); // an exact fit leaves nothing over
        Assert.Equal(Math.Floor(m.CellHeight(800)), m.CellHeight(800));   // never a fractional pixel
    }

    [Fact]
    public void The_highlight_frame_is_inset_by_the_small_spacing_from_each_side()
        => Assert.Equal(116 - 8, new DesktopMetrics(64).FrameWidth(116));

    [Fact]
    public void The_icon_size_setting_is_kept_snapped_and_defaults_to_48()
    {
        var hive = new Vibe.Registry.Hive("HKCU");
        Assert.Equal(48, DesktopSettingsStore.Load(hive).IconSize);
        DesktopSettingsStore.Save(hive, new DesktopSettings(IconSize: 70));
        Assert.Equal(64, DesktopSettingsStore.Load(hive).IconSize);
        hive.SetValue(DesktopSettingsStore.KeyPath, "IconSize", Vibe.Registry.RegistryValueKind.DWord, 9999u);
        Assert.Equal(48, DesktopSettingsStore.Load(hive).IconSize);     // nonsense is ignored
    }
}
