using System.Net.Sockets;
using System.Text;
using Vde.Compositor;

namespace Vde.Compositor.Tests;

public class VwmClientTests
{
    private const string Kate = "{\"event\":\"window\",\"id\":1,\"title\":\"a \\\"quoted\\\" title\",\"app_id\":\"kate\",\"active\":true,\"minimized\":false,\"maximized\":false,\"fullscreen\":false,\"x11\":false,\"x\":1,\"y\":2,\"width\":3,\"height\":4}";

    [Fact]
    public void Window_events_build_the_list_in_first_seen_order_and_closed_removes()
    {
        using var client = new VwmClient();
        var changes = 0;
        client.Changed += (_, _) => changes++;

        client.Handle(Kate);
        client.Handle(Kate.Replace("\"id\":1", "\"id\":2").Replace("kate", "konsole").Replace("\"active\":true", "\"active\":false"));
        client.Handle(Kate.Replace("\"minimized\":false", "\"minimized\":true"));   // id 1 changes: keeps its place

        Assert.Equal(new[] { "1", "2" }, client.Windows.Select(w => w.Id));
        Assert.Equal("a \"quoted\" title", client.Windows[0].Title);
        Assert.True(client.Windows[0].IsMinimized);
        Assert.Equal("konsole", client.Windows[1].AppId);

        client.Handle("{\"event\":\"closed\",\"id\":1}");
        Assert.Equal(new[] { "2" }, client.Windows.Select(w => w.Id));
        Assert.Equal(4, changes);

        client.Handle("not json");
        client.Handle("{\"event\":\"future\",\"id\":9}");
        Assert.Single(client.Windows);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Connect_is_false_without_a_compositor()
    {
        using var client = new VwmClient();
        Assert.False(client.Connect(Path.Combine(Path.GetTempPath(), "no-such-vwm-" + Guid.NewGuid().ToString("N"))));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Over_a_real_socket_events_arrive_commands_leave_and_a_dropped_connection_clears_the_list()
    {
        var path = Path.Combine(Path.GetTempPath(), "vwm-test-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            using var client = new VwmClient();
            var changed = new SemaphoreSlim(0);
            var disconnected = new SemaphoreSlim(0);
            client.Changed += (_, _) => changed.Release();
            client.Disconnected += (_, _) => disconnected.Release();
            Assert.True(client.Connect(path));
            using var server = await listener.AcceptAsync();

            await server.SendAsync(Encoding.UTF8.GetBytes(Kate + "\n"));
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("kate", Assert.Single(client.Windows).AppId);

            client.Activate("1");
            client.Close("not-a-number");   // never reaches the wire
            client.Minimize("1");
            var received = new byte[64];
            var total = 0;
            var expected = "activate 1\nminimize 1\n";
            while (total < expected.Length)
                total += await server.ReceiveAsync(received.AsMemory(total), SocketFlags.None);
            Assert.Equal(expected, Encoding.ASCII.GetString(received, 0, total));

            server.Dispose();
            Assert.True(await disconnected.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(client.Windows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_start_event_reaches_the_handler_and_a_flyout_is_placed_once_per_showing()
    {
        var path = Path.Combine(Path.GetTempPath(), "vwm-test-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            using var client = new VwmClient();
            var starts = new SemaphoreSlim(0);
            var changed = new SemaphoreSlim(0);
            client.StartRequested += (_, _) => starts.Release();
            client.Changed += (_, _) => changed.Release();
            Assert.True(client.Connect(path));
            using var server = await listener.AcceptAsync();

            client.HandleStart();
            client.AnnouncePopup(77, "Start", 668, 460);
            await server.SendAsync(Encoding.UTF8.GetBytes("{\"event\":\"start\"}\n"));
            Assert.True(await starts.WaitAsync(TimeSpan.FromSeconds(5)));

            // the flyout's window appears (twice: it changes), and is placed once; another window of that process is not touched
            string Window(int id, string title) => "{\"event\":\"window\",\"id\":" + id + ",\"title\":\"" + title + "\",\"app_id\":\"vexplore\",\"active\":true,\"minimized\":false,\"maximized\":false,\"fullscreen\":false,\"x11\":false,\"pid\":77,\"x\":0,\"y\":0,\"width\":584,\"height\":620}\n";
            await server.SendAsync(Encoding.UTF8.GetBytes(Window(9, "Taskbar") + Window(10, "Start") + Window(10, "Start")));
            for (var i = 0; i < 3; i++) Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));

            var expected = "handle-start\npopup 10 668 460\n";
            var received = new byte[128];
            var total = 0;
            while (total < expected.Length)
                total += await server.ReceiveAsync(received.AsMemory(total), SocketFlags.None);
            Assert.Equal(expected, Encoding.ASCII.GetString(received, 0, total));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
