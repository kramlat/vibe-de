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

    private static Task Register(DBusConnection connection, string registration)
    {
        using var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s");
        w.WriteString(registration);
        return connection.CallMethodAsync(w.CreateMessage());
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

    // libappindicator, Ayatana and some toolkits register only an object path: the item lives on the sender.
    // This used to throw inside the watcher and end its connection, taking every other icon with it.
    [Fact]
    public async Task AnItemThatRegistersOnlyItsObjectPathIsAdoptedOnTheSendersConnection()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        using var kde = FakeItem.Start(bus, "org.kde.StatusNotifierItem.firstapp", new FakeItem.Property("Id", "kde"), new("IconName", "folder"));
        using var indicator = FakeItem.StartByPath(bus, "/org/ayatana/NotificationItem/one", new FakeItem.Property("Id", "indicator"), new("IconName", "user-trash"));

        var icon = await WaitFor(() => host!.Icons, i => i.Id == "indicator");
        Assert.NotNull(icon);
        Assert.StartsWith(":1.", icon.Service);                                   // the sender's unique name
        Assert.Equal("/org/ayatana/NotificationItem/one", icon.Path);
        Assert.Equal("user-trash", icon.BestIconName);
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "kde"));      // the watcher is alive and kept the other one
        Assert.NotNull(host!.Item(icon));
    }

    [Fact]
    public async Task OneApplicationMayExportSeveralIconsAndTheyStayApart()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        using var one = FakeItem.StartByPath(bus, "/org/ayatana/NotificationItem/a", new FakeItem.Property("Id", "a"), new("IconName", "folder"));
        // a second icon from the same connection
        var two = new FakeItem(one.Connection, "") { Path = "/org/ayatana/NotificationItem/b", RegisterAs = "/org/ayatana/NotificationItem/b", Properties = [new("Id", "b"), new("IconName", "user-home")] };
        one.Connection.AddMethodHandler(two);
        two.RegisterToWatcher();

        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "a"));
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "b"));
        Assert.Equal(2, host!.Icons.Count);
    }

    [Fact]
    public async Task APathRegisteredItemGoesWhenItsConnectionDoes()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        var indicator = FakeItem.StartByPath(bus, "/org/ayatana/NotificationItem/gone", new FakeItem.Property("Id", "gone"), new("IconName", "folder"));
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "gone"));

        indicator.Dispose();   // the app quits: its unique name leaves the bus

        await UntilAsync(() => host!.Icons.Count == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a bus name")]
    [InlineData("/")]
    [InlineData("//double//slash")]
    [InlineData("org.kde.StatusNotifierItem-no-such-app")]
    public async Task NonsenseRegistrationsAreRefusedWithoutHarmingTheWatcher(string nonsense)
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        using var liar = bus.Connect();
        try { await Register(liar, nonsense).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (DBusExceptionBase) { }   // an error reply is fine; silence or a dead watcher is not

        using var real = FakeItem.Start(bus, "org.kde.StatusNotifierItem.survivor", new FakeItem.Property("Id", "survivor"), new("IconName", "folder"));
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "survivor"));
    }

    // Apps read these properties before they register (KDE and Ayatana both do). The replies were malformed,
    // so the bus disconnected the watcher the first time one was read: the tray died on the first app.
    [Fact]
    public async Task TheWatcherAnswersItsPropertiesAndSurvivesBeingAsked()
    {
        using var bus = new PrivateBus();
        using var host = await TrayHost.StartAsync(bus.Address);
        using var client = bus.Connect();
        using var item = FakeItem.Start(bus, "org.kde.StatusNotifierItem.listed", new FakeItem.Property("Id", "listed"), new("IconName", "folder"));
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "listed"));

        Assert.True((await Property(client, "IsStatusNotifierHostRegistered")).GetBool());
        Assert.Equal(0, (await Property(client, "ProtocolVersion")).GetInt32());
        var registered = (await Property(client, "RegisteredStatusNotifierItems")).GetArray<string>();
        Assert.Contains("org.kde.StatusNotifierItem.listed", registered);

        var all = await Task.Run(() =>
        {
            using var w = client.GetMessageWriter();
            w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "GetAll", "s");
            w.WriteString("org.kde.StatusNotifierWatcher");
            return client.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(), null);
        });
        Assert.Equal(3, all.Count);

        using var later = FakeItem.Start(bus, "org.kde.StatusNotifierItem.later", new FakeItem.Property("Id", "later"), new("IconName", "folder"));
        Assert.NotNull(await WaitFor(() => host!.Icons, i => i.Id == "later"));   // still serving after all that
    }

    private static Task<VariantValue> Property(DBusConnection connection, string name)
    {
        using var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "Get", "ss");
        w.WriteString("org.kde.StatusNotifierWatcher");
        w.WriteString(name);
        return connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null);
    }
}
