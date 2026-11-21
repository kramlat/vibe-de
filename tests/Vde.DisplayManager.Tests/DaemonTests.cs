using System.Net.Sockets;
using Vde.DisplayManager;

namespace Vde.DisplayManager.Tests;

public class DaemonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vde-dmd-" + Guid.NewGuid().ToString("N"));
    public DaemonTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeProcess : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Terminated, Killed;
        public LaunchPlan Plan { get; }
        public FakeProcess(LaunchPlan plan) => Plan = plan;
        public Task<int> Exited => _exit.Task;
        public void Terminate() { Terminated = true; _exit.TrySetResult(0); }
        public void Kill() { Killed = true; _exit.TrySetResult(-9); }
        public void Exit(int code) => _exit.TrySetResult(code);
    }

    private sealed class FakeSystem : IDmSystem
    {
        public List<string> Calls { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public Dictionary<string, LoginUser> Users { get; } = new()
        {
            ["vdedm"] = new("vdedm", 900, 900, "/var/lib/vdedm", "/usr/bin/nologin", ""),
            ["alice"] = new("alice", 1000, 1000, "/home/alice", "/bin/fish", "Alice"),
        };
        public string Launcher => "/usr/lib/vde/vterm-launch";
        public void SwitchVt(int vt) { lock (Calls) Calls.Add("vt " + vt); }
        public void GiveTty(string tty, int uid, int gid) { lock (Calls) Calls.Add($"tty {Path.GetFileName(tty)} {uid}"); if (tty.EndsWith(".sock")) File.SetUnixFileMode(tty, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        public IManagedProcess Start(LaunchPlan plan) { var p = new FakeProcess(plan); lock (Processes) Processes.Add(p); return p; }
        public LoginUser? FindUser(string name) => Users.GetValueOrDefault(name);
    }

    private sealed class FakeAuth : IAuthSession
    {
        public Queue<AuthEvent> Script { get; }
        public bool Closed; public SessionInfo? Opened;
        public FakeAuth(params AuthEvent[] script) => Script = new(script);
        public Task<AuthEvent> NextAsync(CancellationToken cancel = default) => Task.FromResult(Script.Dequeue());
        public void Respond(string? answer) { }
        public IReadOnlyDictionary<string, string> OpenSession(SessionInfo info) { Opened = info; return new Dictionary<string, string> { ["XDG_SESSION_ID"] = "c1" }; }
        public void Close() => Closed = true;
        public void Dispose() => Close();
    }

    private sealed class FakeAuthenticator(Func<string, FakeAuth> make) : IAuthenticator
    {
        public List<FakeAuth> Sessions { get; } = [];
        public IAuthSession Begin(string username) { var s = make(username); lock (Sessions) Sessions.Add(s); return s; }
    }

    private static readonly SessionEntry Vde = new("vde", "VDE", "", ["start-vde"], SessionKind.Wayland, "VDE");

    private (Daemon daemon, FakeSystem system, FakeAuthenticator login, FakeAuthenticator greeterPam, FakeAuthenticator auto, DmConfig config) Make(DmConfig? config = null)
    {
        config ??= new DmConfig { SocketPath = Path.Combine(_dir, "greeter.sock"), Vt = 3 };
        var system = new FakeSystem();
        var login = new FakeAuthenticator(_ => new FakeAuth(new Prompt(true, "Password: "), new Authenticated()));
        var greeterPam = new FakeAuthenticator(_ => new FakeAuth(new Authenticated()));
        var auto = new FakeAuthenticator(_ => new FakeAuth(new Authenticated()));
        var daemon = new Daemon(config, login, greeterPam, auto, system, () => [Vde], TextWriter.Null)
        { RestartDelay = TimeSpan.FromMilliseconds(20), GreeterExitWait = TimeSpan.FromMilliseconds(200) };
        return (daemon, system, login, greeterPam, auto, config);
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 10 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task<GreeterResponse> Ask(NetworkStream stream, GreeterRequest request)
    {
        await stream.WriteAsync(Greetd.Encode(request));
        return Greetd.DecodeResponse((await Greetd.ReadFrameAsync(stream, new CancellationTokenSource(5000).Token))!);
    }

    private static async Task<NetworkStream> Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return new NetworkStream(socket, ownsSocket: true);
    }

    [Fact]
    public async Task The_greeter_logs_someone_in_the_greeter_ends_and_the_users_desktop_starts_on_the_same_terminal()
    {
        var (daemon, system, login, greeterPam, _, config) = Make();
        var ready = new SemaphoreSlim(0);
        daemon.GreeterReady += () => ready.Release();
        var started = new TaskCompletionSource<string>();
        daemon.SessionStarted += u => started.TrySetResult(u);
        using var stop = new CancellationTokenSource();
        var run = daemon.RunAsync(stop.Token);

        Assert.True(await ready.WaitAsync(5000));
        var greeter = system.Processes[0];
        Assert.Equal("greeter", greeter.Plan.Environment["XDG_SESSION_CLASS"]);
        Assert.Equal("/dev/tty3", greeter.Plan.Tty);
        Assert.Equal("greeter", greeterPam.Sessions[0].Opened!.Class);

        await using (var stream = await Connect(config.SocketPath))
        {
            Assert.IsType<AuthMessage>(await Ask(stream, new CreateSession("alice")));
            Assert.IsType<Success>(await Ask(stream, new PostAuthMessageResponse("pw")));
            Assert.IsType<Success>(await Ask(stream, new StartSession(["start-vde"], ["XDG_X=1"])));
        }

        Assert.Equal("alice", await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(greeter.Terminated);                       // the greeter is told to go once the login is accepted
        var session = system.Processes[1];
        Assert.Equal("alice", session.Plan.UserName);
        Assert.Equal("c1", session.Plan.Environment["XDG_SESSION_ID"]);   // the PAM session's environment
        Assert.Equal("VDE", session.Plan.Environment["XDG_CURRENT_DESKTOP"]);
        Assert.Equal("1", session.Plan.Environment["XDG_X"]);
        Assert.Contains("tty tty3 1000", system.Calls);        // the terminal belongs to alice while she is on it
        Assert.False(login.Sessions[0].Closed);

        // she logs out: her PAM session closes, the terminal goes back, and the greeter returns
        session.Exit(0);
        await Until(() => login.Sessions[0].Closed);
        await Until(() => system.Processes.Count >= 3);
        Assert.Equal("greeter", system.Processes[2].Plan.Environment["XDG_SESSION_CLASS"]);

        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task A_greeter_that_dies_without_a_login_is_started_again()
    {
        var (daemon, system, _, _, _, _) = Make();
        using var stop = new CancellationTokenSource();
        var run = daemon.RunAsync(stop.Token);
        await Until(() => system.Processes.Count >= 1);

        system.Processes[0].Exit(1);
        await Until(() => system.Processes.Count >= 2);

        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Autologin_skips_the_greeter_the_first_time_only()
    {
        var config = new DmConfig { SocketPath = Path.Combine(_dir, "g.sock"), Vt = 2, AutoLoginUser = "alice" };
        var (daemon, system, _, _, auto, _) = Make(config);
        var started = new TaskCompletionSource<string>();
        daemon.SessionStarted += u => started.TrySetResult(u);
        var ready = new SemaphoreSlim(0);
        daemon.GreeterReady += () => ready.Release();
        using var stop = new CancellationTokenSource();
        var run = daemon.RunAsync(stop.Token);

        Assert.Equal("alice", await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(auto.Sessions);
        Assert.Equal("alice", system.Processes[0].Plan.UserName);   // no greeter was started first
        Assert.Equal(new[] { "start-vde" }, system.Processes[0].Plan.Command.Skip(9).ToArray());

        system.Processes[0].Exit(0);
        Assert.True(await ready.WaitAsync(5000));   // logging out shows the greeter, not another autologin
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_no_session_starts()
    {
        var (daemon, system, login, greeterPam, auto, config) = Make();
        var denyAuth = new FakeAuthenticator(_ => new FakeAuth(new Prompt(true, "Password: "), new Failed("Authentication failure", true)));
        var d2 = new Daemon(config, denyAuth, greeterPam, auto, system, () => [Vde], TextWriter.Null)
        { RestartDelay = TimeSpan.FromMilliseconds(20), FailureDelay = TimeSpan.Zero };
        var ready = new SemaphoreSlim(0);
        d2.GreeterReady += () => ready.Release();
        using var stop = new CancellationTokenSource();
        var run = d2.RunAsync(stop.Token);
        Assert.True(await ready.WaitAsync(5000));

        await using (var stream = await Connect(config.SocketPath))
        {
            Assert.IsType<AuthMessage>(await Ask(stream, new CreateSession("alice")));
            var failed = Assert.IsType<ErrorResponse>(await Ask(stream, new PostAuthMessageResponse("nope")));
            Assert.Equal(ErrorType.AuthError, failed.Type);
            Assert.IsType<ErrorResponse>(await Ask(stream, new StartSession(["start-vde"], [])));   // still not logged in
        }
        Assert.Single(system.Processes);   // only the greeter

        stop.Cancel();
        await run;
    }
}
