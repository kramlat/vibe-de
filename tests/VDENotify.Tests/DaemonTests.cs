using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Vde.Shell.Notifications;
using Vibe.Registry;
using VdeNotify;

namespace VDENotify.Tests;

internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;
    private readonly string _config;
    public string Address { get; }

    public PrivateBus()
    {
        _config = Path.Combine(Path.GetTempPath(), "vdenotify-bus-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(_config, """
            <busconfig>
              <type>session</type>
              <listen>unix:tmpdir=/tmp</listen>
              <auth>EXTERNAL</auth>
              <policy context="default"><allow send_destination="*" eavesdrop="true"/><allow eavesdrop="true"/><allow own="*"/></policy>
            </busconfig>
            """);
        var info = new ProcessStartInfo("dbus-daemon") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "--nofork", "--print-address=1", "--config-file=" + _config })
            info.ArgumentList.Add(a);
        _daemon = Process.Start(info)!;
        Address = _daemon.StandardOutput.ReadLine()!.Trim();
    }

    public void Kill() { try { _daemon.Kill(); } catch (InvalidOperationException) { } }

    // A real command-line client, so the daemon is exercised the way applications use it.
    public async Task<(int exit, string output)> Busctl(params string[] call)
    {
        var info = new ProcessStartInfo("/usr/bin/busctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--address=" + Address, "--user", "call", "org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications" }.Concat(call))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var text = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, text);
    }

    public Task<(int exit, string output)> Notify(string app, string summary, string body = "", string urgency = "1")
        => Busctl("Notify", "susssasa{sv}i", app, "0", "", summary, body, "0", "1", "urgency", "y", urgency, "5000");

    public void Dispose()
    {
        Kill();
        _daemon.Dispose();
        File.Delete(_config);
    }
}

internal sealed class Running : IDisposable
{
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    public CancellationTokenSource Stop { get; } = new();
    public Task<int> Exit { get; }

    public Running(PrivateBus bus, IHive? registry = null, Dictionary<string, string>? env = null, params string[] extra)
    {
        Exit = NotifyDaemon.RunAsync([.. new[] { "--bus", bus.Address }, .. (registry is null ? ["--no-registry"] : Array.Empty<string>()), .. extra],
            Out, Err, Stop.Token, k => env is not null && env.TryGetValue(k, out var v) ? v : null, registry);
    }

    public void WaitUntilServing()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Err.ToString().Contains("serving") && !Exit.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never started serving: " + Err);
            Thread.Sleep(10);
        }
    }

    public void WaitForOutput(string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Out.ToString().Contains(text))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"never logged '{text}': {Out}");
            Thread.Sleep(10);
        }
    }

    public void Dispose() => Stop.Cancel();
}

public class DaemonTests
{
    [Fact]
    public async Task It_serves_the_name_logs_what_it_receives_and_releases_the_name_when_stopped()
    {
        using var bus = new PrivateBus();
        using var run = new Running(bus);
        run.WaitUntilServing();

        var info = await bus.Busctl("GetServerInformation");
        Assert.Equal(0, info.exit);
        Assert.Contains("\"VDE\"", info.output);

        var sent = await bus.Notify("Mail", "New message", "<b>Hello</b> there", urgency: "2");
        Assert.Equal(0, sent.exit);
        run.WaitForOutput("notification 1 from Mail: New message - Hello there [Critical]");   // markup stripped for the log

        run.Stop.Cancel();
        Assert.Equal(NotifyDaemon.Ok, await run.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("stopped", run.Err.ToString());

        using var second = new Running(bus);   // the name is free again
        second.WaitUntilServing();
        Assert.False(second.Exit.IsCompleted);
    }

    [Fact]
    public async Task Settings_come_from_the_registry_and_follow_it_live()
    {
        using var bus = new PrivateBus();
        var hive = new Hive("HKCU");
        NotificationSettingsStore.Save(hive, new NotificationSettings { DoNotDisturb = true });
        using var run = new Running(bus, registry: hive);
        run.WaitUntilServing();

        await bus.Notify("Chat", "one");
        run.WaitForOutput("one [Normal, silenced]");   // do not disturb: listed, not shown

        hive.SetValue(NotificationSettingsStore.KeyPath, "DoNotDisturb", RegistryValueKind.DWord, 0u);   // Settings flips it
        await bus.Notify("Chat", "two");
        run.WaitForOutput("two [Normal]");
        Assert.DoesNotContain("two [Normal, silenced]", run.Out.ToString());
    }

    [Fact]
    public async Task A_second_daemon_exits_with_a_clear_error_and_leaves_the_first_alone()
    {
        using var bus = new PrivateBus();
        using var first = new Running(bus);
        first.WaitUntilServing();

        using var second = new Running(bus);
        Assert.Equal(NotifyDaemon.BusError, await second.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("already owns", second.Err.ToString());

        Assert.Equal(0, (await bus.Notify("A", "still served")).exit);   // the first one carries on
    }

    [Fact]
    public async Task Losing_the_bus_is_an_error_exit_so_systemd_restarts_it()
    {
        using var bus = new PrivateBus();
        using var run = new Running(bus);
        run.WaitUntilServing();

        bus.Kill();

        Assert.Equal(NotifyDaemon.LostBus, await run.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("lost the connection", run.Err.ToString());
    }

    [Fact]
    public async Task It_tells_systemd_when_it_is_ready()
    {
        using var bus = new PrivateBus();
        var dir = Path.Combine(Path.GetTempPath(), "vdenotify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var notify = Path.Combine(dir, "notify.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(notify));
            listener.ReceiveTimeout = 10000;

            using var run = new Running(bus, env: new Dictionary<string, string> { ["NOTIFY_SOCKET"] = notify });
            var buffer = new byte[256];
            var text = Encoding.UTF8.GetString(buffer, 0, listener.Receive(buffer));

            Assert.StartsWith("READY=1", text);
            run.Stop.Cancel();
            await run.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Usage_mistakes_exit_1_and_help_exits_0()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();

        Assert.Equal(0, await NotifyDaemon.RunAsync(["--help"], output, error, stop.Token));
        Assert.Contains("usage: VDENotify", output.ToString());
        Assert.Equal(1, await NotifyDaemon.RunAsync(["--bogus"], output, error, stop.Token));
        Assert.Equal(1, await NotifyDaemon.RunAsync(["--bus"], output, error, stop.Token));   // missing value
    }
}
