using System.Net.Sockets;
using System.Text;
using Fhived;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace fhived.Tests;

internal sealed class Run : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fhived-" + Guid.NewGuid().ToString("N"));
    public string Socket => Path.Combine(Dir, "run", "x.sock");
    public string Hive => Path.Combine(Dir, "hive", "H.fhive");
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    public CancellationTokenSource Stop { get; } = new();

    public Run() => Directory.CreateDirectory(Dir);

    public Task<int> Start(Dictionary<string, string>? env, params string[] args)
        => FhivedCommand.RunAsync(args, Out, Err, Stop.Token, k => env is not null && env.TryGetValue(k, out var v) ? v : null);

    public void WaitUntilServing()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Socket))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the daemon never started listening");
            Thread.Sleep(10);
        }
    }

    public RemoteHive Connect() => RemoteHive.Connect(Socket);

    public void Dispose()
    {
        Stop.Cancel();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}

public class FhivedTests
{
    [Fact]
    public async Task The_user_daemon_creates_its_hive_serves_it_and_cleans_up_on_stop()
    {
        using var run = new Run();
        var exit = run.Start(null, "--user", "--socket", run.Socket, "--hive", run.Hive);
        run.WaitUntilServing();

        Assert.True(File.Exists(run.Hive));                                                            // created, as the user hive is
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(run.Socket));   // 0600
        using (var client = run.Connect())
        {
            client.SetValue(@"Software\Vde", "Theme", RegistryValueKind.String, "simple");
            Assert.Equal("simple", client.GetValue(@"Software\Vde", "Theme")!.Data);
        }

        run.Stop.Cancel();
        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(run.Socket));                                                         // no stale socket left behind
        Assert.Contains("serving", run.Err.ToString());
        Assert.Contains("stopped", run.Err.ToString());

        using var reopened = Hive.OpenShared("HKCU", run.Hive, watch: false);                          // and the data is in the file
        Assert.Equal("simple", reopened.GetValue(@"Software\Vde", "Theme")!.Data);
    }

    [Fact]
    public async Task The_system_daemon_serves_a_hive_it_may_not_write_as_read_only()
    {
        using var run = new Run();
        using (var seed = Hive.OpenShared("HKLM", run.Hive, watch: false))
            seed.SetValue("Software\\Vde", "Version", RegistryValueKind.String, "1");

        var exit = run.Start(null, "--system", "--socket", run.Socket, "--hive", run.Hive);   // the test is not root: read-only by default
        run.WaitUntilServing();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                     | UnixFileMode.OtherRead | UnixFileMode.OtherWrite, File.GetUnixFileMode(run.Socket));   // 0666
        using var client = run.Connect();
        Assert.True(client.IsReadOnly);
        Assert.Equal("1", client.GetValue("Software\\Vde", "Version")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => client.SetValue("Software\\Vde", "Version", RegistryValueKind.String, "2"));
        Assert.Contains("(read-only)", run.Err.ToString());

        run.Stop.Cancel();
        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_missing_system_hive_is_served_empty_and_never_created_by_a_non_root_daemon()
    {
        using var run = new Run();
        // A daemon that is not root is read-only, so it has nothing to create; creating the system hive is mkhive's job.
        var exit = run.Start(null, "--system", "--socket", run.Socket, "--hive", run.Hive);
        run.WaitUntilServing();
        Assert.False(File.Exists(run.Hive));
        run.Stop.Cancel();
        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Usage_mistakes_exit_1_and_help_exits_0()
    {
        using var run = new Run();

        Assert.Equal(1, await run.Start(null));                                            // neither --system nor --user
        Assert.Equal(1, await run.Start(null, "--system", "--user"));
        Assert.Equal(1, await run.Start(null, "--user", "--bogus"));
        Assert.Equal(1, await run.Start(null, "--user", "--socket"));                      // missing value
        Assert.Equal(1, await run.Start(null, "--user", "--idle-exit", "soon"));
        Assert.Equal(1, await run.Start(null, "--user"));                                   // no XDG_RUNTIME_DIR to put the socket in
        Assert.Contains("XDG_RUNTIME_DIR", run.Err.ToString());
        Assert.Equal(0, await run.Start(null, "--help"));
        Assert.Contains("usage: fhived", run.Out.ToString());
    }

    [Fact]
    public async Task The_default_socket_goes_under_xdg_runtime_dir()
    {
        using var run = new Run();
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = Path.Combine(run.Dir, "runtime"), ["XDG_CONFIG_HOME"] = Path.Combine(run.Dir, "config") };
        var exit = run.Start(env, "--user");
        var socket = Path.Combine(run.Dir, "runtime", "fhive", "user.sock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(socket) && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.True(File.Exists(socket));
        Assert.True(File.Exists(Path.Combine(run.Dir, "config", "hive", "User.fhive")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(socket)!));   // 0700

        run.Stop.Cancel();
        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_corrupt_hive_is_a_hive_error_and_is_left_alone()
    {
        using var run = new Run();
        Directory.CreateDirectory(Path.GetDirectoryName(run.Hive)!);
        File.WriteAllText(run.Hive, "not a hive");

        var exit = await run.Start(null, "--user", "--socket", run.Socket, "--hive", run.Hive);

        Assert.Equal(FhivedCommand.HiveError, exit);
        Assert.Equal("not a hive", File.ReadAllText(run.Hive));
    }

    [Fact]
    public async Task Socket_activation_without_a_socket_from_systemd_is_a_clear_error()
    {
        using var run = new Run();

        var exit = await run.Start(null, "--user", "--socket-activation", "--hive", run.Hive);

        Assert.Equal(FhivedCommand.ListenError, exit);
        Assert.Contains("LISTEN_FDS", run.Err.ToString());
    }

    [Fact]
    public async Task Idle_exit_stops_a_daemon_nobody_uses_but_not_one_with_a_client()
    {
        using var run = new Run();
        var exit = run.Start(null, "--user", "--socket", run.Socket, "--hive", run.Hive, "--idle-exit", "2");
        run.WaitUntilServing();

        using (var client = run.Connect())
        {
            await Task.Delay(3500);
            Assert.False(exit.IsCompleted, "exited although a client was connected");
            Assert.False(client.KeyExists("x"));   // still answering
        }
        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));   // the client left: now it goes
    }

    [Fact]
    public async Task Readiness_is_announced_to_systemd()
    {
        using var run = new Run();
        var notify = Path.Combine(run.Dir, "notify.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(notify));
        listener.ReceiveTimeout = 10000;

        var exit = run.Start(new Dictionary<string, string> { ["NOTIFY_SOCKET"] = notify }, "--user", "--socket", run.Socket, "--hive", run.Hive);
        var buffer = new byte[256];
        var text = Encoding.UTF8.GetString(buffer, 0, listener.Receive(buffer));

        Assert.StartsWith("READY=1", text);
        run.Stop.Cancel();
        await exit.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
