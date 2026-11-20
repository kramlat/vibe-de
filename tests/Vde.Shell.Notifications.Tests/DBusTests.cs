using System.Diagnostics;
using Tmds.DBus.Protocol;
using Vde.Shell.Notifications;

namespace Vde.Shell.Notifications.Tests;

// A private session bus, so tests never touch (or depend on) the real desktop's.
internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;
    private readonly string _config;
    public string Address { get; }

    public PrivateBus()
    {
        _config = Path.Combine(Path.GetTempPath(), "vde-bus-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(_config, """
            <busconfig>
              <type>session</type>
              <listen>unix:tmpdir=/tmp</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);
        var info = new ProcessStartInfo("dbus-daemon") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("--nofork");
        info.ArgumentList.Add("--print-address=1");
        info.ArgumentList.Add("--config-file=" + _config);
        _daemon = Process.Start(info)!;
        Address = _daemon.StandardOutput.ReadLine()!.Trim();
    }

    public DBusConnection Connect()
    {
        var connection = new DBusConnection(new DBusConnectionOptions(Address) { AutoConnect = false });
        connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
        return connection;
    }

    public void Dispose()
    {
        try { _daemon.Kill(); } catch (InvalidOperationException) { }
        _daemon.Dispose();
        File.Delete(_config);
    }
}

public sealed class Client(DBusConnection connection) : IDisposable
{
    private const string Dest = "org.freedesktop.Notifications", Path = "/org/freedesktop/Notifications", Iface = "org.freedesktop.Notifications";

    public DBusConnection Connection { get; } = connection;
    public string UniqueName => Connection.GetType().GetProperty("UniqueName")?.GetValue(Connection)?.ToString() ?? "";

    public Task<uint> NotifyAsync(string app, string summary, uint replaces = 0, string body = "", string[]? actions = null,
        int timeout = -1, (string name, object value)[]? hints = null)
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, Iface, "Notify", "susssasa{sv}i");
        w.WriteString(app);
        w.WriteUInt32(replaces);
        w.WriteString("");
        w.WriteString(summary);
        w.WriteString(body);
        w.WriteArray(actions ?? []);
        var dict = w.WriteDictionaryStart();
        foreach (var (name, value) in hints ?? [])
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(name);
            switch (value)
            {
                case byte b: w.WriteVariantByte(b); break;
                case int i: w.WriteVariantInt32(i); break;
                case uint u: w.WriteVariantUInt32(u); break;
                case bool flag: w.WriteVariantBool(flag); break;
                case string s: w.WriteVariantString(s); break;
                default: throw new ArgumentException("hint type");
            }
        }
        w.WriteDictionaryEnd(dict);
        w.WriteInt32(timeout);
        return Connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);
    }

    public Task CloseAsync(uint id)
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, Iface, "CloseNotification", "u");
        w.WriteUInt32(id);
        return Connection.CallMethodAsync(w.CreateMessage());
    }

    public Task<string[]> CapabilitiesAsync()
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, Iface, "GetCapabilities", null);
        return Connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadArrayOfString(), null);
    }

    public Task<(string name, string vendor, string version, string spec)> ServerInfoAsync()
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, Iface, "GetServerInformation", null);
        return Connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            return (r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        }, null);
    }

    public Task<string> IntrospectAsync()
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, "org.freedesktop.DBus.Introspectable", "Introspect", null);
        return Connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadString(), null);
    }

    // Raw call with a deliberately wrong signature.
    public Task CallWrongAsync()
    {
        using var w = Connection.GetMessageWriter();
        w.WriteMethodCallHeader(Dest, Path, Iface, "Notify", "s");
        w.WriteString("only one string");
        return Connection.CallMethodAsync(w.CreateMessage());
    }

    public async Task<IDisposable> WatchClosedAsync(Action<uint, uint> handler)
        => await Connection.WatchSignalAsync(Dest, Path, Iface, "NotificationClosed",
            (Message m, object? _) => { var r = m.GetBodyReader(); return (r.ReadUInt32(), r.ReadUInt32()); },
            (Notification<(uint, uint)> n) => { if (n.HasValue) handler(n.Value.Item1, n.Value.Item2); return ValueTask.CompletedTask; }, null, false, ObserverFlags.None);

    public async Task<IDisposable> WatchActionsAsync(Action<uint, string> handler)
        => await Connection.WatchSignalAsync(Dest, Path, Iface, "ActionInvoked",
            (Message m, object? _) => { var r = m.GetBodyReader(); return (r.ReadUInt32(), r.ReadString()); },
            (Notification<(uint, string)> n) => { if (n.HasValue) handler(n.Value.Item1, n.Value.Item2); return ValueTask.CompletedTask; }, null, false, ObserverFlags.None);

    public void Dispose() => Connection.Dispose();
}

public class DBusTests
{
    private static async Task<(PrivateBus bus, NotificationService center, NotificationsDBusServer server)> Start()
    {
        var bus = new PrivateBus();
        var center = new NotificationService(settings: new NotificationSettings { MaxPerApp = 100 });
        var server = await NotificationsDBusServer.StartAsync(center, bus.Address);
        return (bus, center, server);
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public async Task The_server_describes_itself_and_can_be_introspected()
    {
        var (bus, _, server) = await Start();
        using (bus) using (server)
        using (var client = new Client(bus.Connect()))
        {
            var info = await client.ServerInfoAsync();
            Assert.Equal(("VDE", "Vibe Desktop Environment", "0.1", "1.2"), info);
            var caps = await client.CapabilitiesAsync();
            Assert.Contains("actions", caps);
            Assert.Contains("body-markup", caps);
            var xml = await client.IntrospectAsync();
            Assert.Contains("org.freedesktop.Notifications", xml);
            Assert.Contains("NotificationClosed", xml);
        }
    }

    [Fact]
    public async Task Notify_reaches_the_center_with_hints_and_actions_read_properly()
    {
        var (bus, center, server) = await Start();
        using (bus) using (server)
        using (var client = new Client(bus.Connect()))
        {
            var id = await client.NotifyAsync("Mail", "New message", body: "<b>Hi</b>", actions: ["default", "Open", "reply", "Reply"], timeout: 8000,
                hints: [("urgency", (byte)2), ("resident", true), ("category", "email.arrived"), ("desktop-entry", "mail"), ("image-path", "/tmp/a.png")]);

            var n = Assert.Single(center.Active);
            Assert.Equal(id, n.Id);
            Assert.Equal(("Mail", "New message", "<b>Hi</b>"), (n.AppName, n.Summary, n.Body));
            Assert.Equal(Urgency.Critical, n.Urgency);
            Assert.True(n.Resident);
            Assert.Equal(TimeSpan.FromSeconds(8), n.Timeout is null ? TimeSpan.Zero : n.Timeout);   // critical + explicit timeout is honoured
            Assert.Equal(("email.arrived", "mail", "/tmp/a.png"), (n.Category, n.DesktopEntry, n.ImagePath));
            Assert.Equal(new[] { "default", "reply" }, n.Actions.Select(a => a.Key));
            Assert.Equal("Reply", n.Actions[1].Label);
        }
    }

    [Theory]
    [InlineData(0, Urgency.Low)]
    [InlineData(1, Urgency.Normal)]
    [InlineData(2, Urgency.Critical)]
    public async Task Urgency_is_understood_whatever_integer_type_the_application_used(int level, Urgency expected)
    {
        var (bus, center, server) = await Start();
        using (bus) using (server)
        using (var client = new Client(bus.Connect()))
        {
            await client.NotifyAsync("A", "byte", hints: [("urgency", (byte)level)]);
            await client.NotifyAsync("A", "int", hints: [("urgency", level)]);
            await client.NotifyAsync("A", "uint", hints: [("urgency", (uint)level)]);

            Assert.All(center.Active, n => Assert.Equal(expected, n.Urgency));
        }
    }

    [Fact]
    public async Task Only_the_sender_may_replace_or_close_its_notification()
    {
        var (bus, center, server) = await Start();
        using (bus) using (server)
        using (var owner = new Client(bus.Connect()))
        using (var other = new Client(bus.Connect()))
        {
            var id = await owner.NotifyAsync("Owner", "mine");

            Assert.Equal(id, await owner.NotifyAsync("Owner", "updated", replaces: id));            // the owner may replace it
            Assert.Equal("updated", center.Active.Single(n => n.Id == id).Summary);

            var intruder = await other.NotifyAsync("Other", "hijack", replaces: id);                 // someone else asking to replace it
            Assert.NotEqual(id, intruder);                                                            // gets a notification of their own
            Assert.Equal("updated", center.Active.Single(n => n.Id == id).Summary);                  // and the original is untouched

            var denied = await Assert.ThrowsAsync<DBusErrorReplyException>(() => other.CloseAsync(id));
            Assert.Equal("org.freedesktop.DBus.Error.AccessDenied", denied.ErrorName);
            Assert.Contains(center.Active, n => n.Id == id);

            await owner.CloseAsync(id);
            Assert.DoesNotContain(center.Active, n => n.Id == id);
            await owner.CloseAsync(424242);                                                            // closing something that is gone is fine
        }
    }

    [Fact]
    public async Task Closing_and_action_signals_are_sent_with_the_spec_reasons()
    {
        var (bus, center, server) = await Start();
        using (bus) using (server)
        using (var client = new Client(bus.Connect()))
        {
            var closed = new List<(uint id, uint reason)>();
            var actions = new List<(uint id, string key)>();
            using var w1 = await client.WatchClosedAsync((id, reason) => { lock (closed) closed.Add((id, reason)); });
            using var w2 = await client.WatchActionsAsync((id, key) => { lock (actions) actions.Add((id, key)); });

            var a = await client.NotifyAsync("A", "one", actions: ["default", "Open"]);
            var b = await client.NotifyAsync("A", "two");
            var c = await client.NotifyAsync("A", "three");

            center.InvokeAction(a, "default");                       // the user clicked it
            center.Close(b, CloseReason.Expired);
            await client.CloseAsync(c);                               // the application closed it

            WaitFor(() => { lock (closed) return closed.Count == 3; }, "three NotificationClosed signals");
            lock (actions) Assert.Equal((a, "default"), Assert.Single(actions));
            lock (closed)
            {
                Assert.Contains((a, 2u), closed);   // dismissed (after the action)
                Assert.Contains((b, 1u), closed);   // expired
                Assert.Contains((c, 3u), closed);   // closed by the call
            }
        }
    }

    [Fact]
    public async Task A_second_daemon_cannot_take_the_name_and_bad_calls_get_errors_not_crashes()
    {
        var (bus, center, server) = await Start();
        using (bus) using (server)
        using (var client = new Client(bus.Connect()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationsDBusServer.StartAsync(new NotificationService(), bus.Address));

            await Assert.ThrowsAsync<DBusErrorReplyException>(() => client.CallWrongAsync());
            Assert.Equal(1u, await client.NotifyAsync("A", "still works"));   // the server carried on
            Assert.Single(center.Active);
        }
    }

    [Fact]
    public async Task Real_command_line_clients_work_too()
    {
        if (!File.Exists("/usr/bin/busctl"))
            return;
        var (bus, center, server) = await Start();
        using (bus) using (server)
        {
            var info = new ProcessStartInfo("/usr/bin/busctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "--address=" + bus.Address, "--user", "call", "org.freedesktop.Notifications", "/org/freedesktop/Notifications",
                         "org.freedesktop.Notifications", "Notify", "susssasa{sv}i", "busctl", "0", "", "From busctl", "hello", "0", "1", "urgency", "y", "2", "5000" })
                info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, output);
            var n = Assert.Single(center.Active);
            Assert.Equal(("busctl", "From busctl", "hello"), (n.AppName, n.Summary, n.Body));
            Assert.Equal(Urgency.Critical, n.Urgency);
        }
    }
}
