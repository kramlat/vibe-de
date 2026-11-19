namespace Vde.Shell.Tray.Tests;

public class TrayModelTests
{
    [Fact]
    public void AttentionStatusPrefersAttentionIcon()
    {
        var icon = new TrayIcon("svc", "/i")
        {
            IconName = "normal-name",
            AttentionIconName = "attention-name",
            Status = "NeedsAttention",
        };
        Assert.Equal("attention-name", icon.BestIconName);
        var icon2 = icon with { Status = "Active" };
        Assert.Equal("normal-name", icon2.BestIconName);
    }

    [Fact]
    public void BestPixmapPrefersTheAttentionImageOnlyWhileNeeded()
    {
        var plain = new TrayPixmap(20, 20, new byte[20 * 20 * 4]);
        var attention = new TrayPixmap(8, 8, new byte[8 * 8 * 4]);
        var icon = new TrayIcon("svc", "/i") { IconPixmap = plain, AttentionIconPixmap = attention, Status = "NeedsAttention" };
        Assert.Equal(icon.AttentionIconPixmap, icon.BestPixmap);
        var resting = icon with { Status = "Active" };
        Assert.Equal(icon.IconPixmap, resting.BestPixmap);
        var nothing = new TrayIcon("svc", "/i");
        Assert.Null(nothing.BestPixmap);
    }

    [Fact]
    public void DisplayNamePrefersTooltipTitleThenTitleThenId()
    {
        var icon = new TrayIcon("org.kde.StatusNotifierItem.app-1", "/i") { Id = "My Widget", Title = "Widget" };
        Assert.Equal("Widget", icon.DisplayName);
        var plain = icon with { Title = "" };
        Assert.Equal("My Widget", plain.DisplayName);
        var told = plain with { ToolTip = new TrayTooltip("", null, "Full title", "sub text") };
        Assert.Equal("Full title", told.DisplayName);
    }

    [Fact]
    public void SeparatorDisplayLabelIsEmpty() => Assert.Equal("", TrayMenuItem.Separator.DisplayLabel);

    [Fact]
    public void MnemonicsAreStrippedFromLabels()
    {
        var item = new TrayMenuItem(7, "S_u_b M_enu__x");
        Assert.Equal("Sub Menu_x", item.DisplayLabel);
    }
}
