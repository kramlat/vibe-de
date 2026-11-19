using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray.Tests;

// Wire-level behaviour against a private bus: registration, icons, clicks and menus, all with fakes
// standing in for real apps.
public class TrayDBusTests
{
    private static byte[] MakePixmap(int side, byte a, byte r, byte g)
    {
        var bytes = new byte[side * side * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = a;
            bytes[i + 1] = r;
            bytes[i + 2] = g;
            bytes[i + 3] = 0;
        }
        return bytes;
    }

    private static async Task<TrayIcon?> WaitFor(Func<IReadOnlyList<TrayIcon>> icons, Func<TrayIcon, bool> match, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            var found = icons().FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(50);
        }
        return null;
    }

    private static async Task UntilAsync(Func<bool> done, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs && !done(); waited += 50)
            await Task.Delay(50);
        Assert.True(done(), "condition not reached");
    }

    [Fact]
    public async Task HostRegistersItemsAndReadsTheirProperties()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);

        using var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.testapp",
            new FakeItem.Property("Id", "Test App"),
            new("Title", "The Tester"),
            new("Status", "Active"),
            new("Category", "ApplicationStatus"),
            new("IconName", "user-trash"));

        var icon = await WaitFor(() => host.Icons, i => i.Id == "Test App");
        Assert.NotNull(icon);
        Assert.Equal("The Tester", icon.Title);
        Assert.Equal("user-trash", icon.BestIconName);
        Assert.Equal("ApplicationStatus", icon.Category);
        Assert.Contains("testapp", icon.Service);
    }

    [Fact]
    public async Task SecondHostIsRefused()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);
        using var second = await TrayHost.StartAsync(bus.Address);
        Assert.Null(second);
    }

    [Fact]
    public async Task PixmapIsReadFromTheItem()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);
        using var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.pixmapapp",
            new FakeItem.Property("Id", "Pixel App"),
            new("IconPixmap", Pixmaps: [(20, 20, MakePixmap(20, 255, 0, 0)), (48, 48, MakePixmap(48, 0, 0, 255))]));

        var icon = await WaitFor(() => host.Icons, i => i.Id == "Pixel App");
        Assert.NotNull(icon);
        Assert.Equal(20, icon.IconPixmap?.Width);   // 20px is the closer candidate for a 24px bar
    }

    [Fact]
    public async Task ActivateReachesTheItem()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);
        using var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.clickapp",
            new FakeItem.Property("Id", "Click App"));
        var icon = await WaitFor(() => host.Icons, i => i.Id == "Click App");
        Assert.NotNull(icon);

        await host.Item(icon)!.ActivateAsync(66, 72);
        await UntilAsync(() => item.Activated == (66, 72));
    }

    [Fact]
    public async Task ItemsVacateWhenTheirConnectionDoes()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);
        var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.ghost",
            new FakeItem.Property("Id", "Ghost"));
        var icon = await WaitFor(() => host.Icons, i => i.Id == "Ghost");
        Assert.NotNull(icon);

        item.Dispose();
        await UntilAsync(() => host.Icons.Count == 0);
    }

    [Fact]
    public async Task ItemMenuIsExposedAndReadThroughTheHost()
    {
        using var bus = new PrivateBus();
        using var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.MenuApp",
            new FakeItem.Property("Id", "Menu App"),
            new("Menu", Menu: "/MenuBar"));
        using var menuFake = FakeMenu.Start(item);
        using var host = await TrayHost.StartAsync(bus.Address);
        Assert.NotNull(host);
        var icon = await WaitFor(() => host.Icons, i => i.Id == "Menu App");
        Assert.NotNull(icon);
        Assert.Equal("/MenuBar", host.Item(icon)!.MenuPath);

        var menu = host.Menu(icon);
        Assert.NotNull(menu);
        var snapshot = await menu.ReadAsync();
        Assert.Equal(2u, snapshot.Revision);
        var root = Assert.Single(snapshot.Items);
        Assert.Equal(3, root.Children.Count);
        Assert.True(root.Children[0].IsSeparator);
        Assert.Equal("Click Me", root.Children[1].DisplayLabel);
        Assert.Equal("Sub", root.Children[2].DisplayLabel);
        var inner = Assert.Single(root.Children[2].Children);
        Assert.Equal("Int_ernal", inner.DisplayLabel);

        await menu.SendEventAsync(1, "clicked", null);
        await UntilAsync(() => menuFake.Clicks.Contains(1));
    }
}
