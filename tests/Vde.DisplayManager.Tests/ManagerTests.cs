using System.Text;
using Vde.DisplayManager;

namespace Vde.DisplayManager.Tests;

public class ProtocolTests
{
    [Fact]
    public void Requests_and_responses_round_trip_through_the_framing()
    {
        foreach (GreeterRequest request in new GreeterRequest[]
                 { new CreateSession("alice"), new PostAuthMessageResponse("pw"), new PostAuthMessageResponse(null), new StartSession(["start-vde", "--x"], ["A=1"]), new CancelSession() })
        {
            var bytes = Greetd.Encode(request);
            Assert.Equal((uint)(bytes.Length - 4), BitConverter.ToUInt32(bytes));   // native-endian length prefix
            Assert.Equal(request.ToString(), RoundTrip(bytes).ToString() is var s && request is StartSession ? request.ToString() : s);
        }
        var start = (StartSession)RoundTrip(Greetd.Encode(new StartSession(["a", "b"], ["K=V"])));
        Assert.Equal(new[] { "a", "b" }, start.Cmd);
        Assert.Equal(new[] { "K=V" }, start.Env);
        Assert.Equal("alice", ((CreateSession)RoundTrip(Greetd.Encode(new CreateSession("alice")))).Username);
        Assert.Null(((PostAuthMessageResponse)RoundTrip(Greetd.Encode(new PostAuthMessageResponse(null)))).Response);

        foreach (GreeterResponse response in new GreeterResponse[] { new Success(), new ErrorResponse(ErrorType.AuthError, "no"), new AuthMessage(AuthMessageType.Secret, "Password: ") })
        {
            var decoded = Greetd.DecodeResponse(Greetd.Encode(response)[4..]);
            Assert.Equal(response, decoded);
        }
    }

    private static GreeterRequest RoundTrip(byte[] framed) => Greetd.DecodeRequest(framed.AsSpan(4));

    [Fact]
    public void The_wire_format_is_what_greetd_greeters_expect()
    {
        var text = Encoding.UTF8.GetString(Greetd.Encode(new AuthMessage(AuthMessageType.Visible, "Login: "))[4..]);
        Assert.Contains("\"type\":\"auth_message\"", text);
        Assert.Contains("\"auth_message_type\":\"visible\"", text);
        Assert.Contains("\"error_type\":\"auth_error\"", Encoding.UTF8.GetString(Greetd.Encode(new ErrorResponse(ErrorType.AuthError, "x"))[4..]));
        // a start_session without env is allowed
        Assert.Empty(((StartSession)Greetd.DecodeRequest("{\"type\":\"start_session\",\"cmd\":[\"x\"]}"u8)).Env);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"nope\"}")]
    [InlineData("{\"type\":\"create_session\"}")]
    [InlineData("{\"type\":\"create_session\",\"username\":5}")]
    [InlineData("{\"type\":\"start_session\",\"cmd\":[1]}")]
    [InlineData("{\"type\":\"start_session\"}")]
    public void Malformed_requests_are_refused_not_crashed_on(string body)
        => Assert.Throws<InvalidDataException>(() => Greetd.DecodeRequest(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task A_stream_of_frames_is_read_one_by_one_and_oversized_or_cut_off_frames_are_refused()
    {
        var stream = new MemoryStream();
        stream.Write(Greetd.Encode(new CreateSession("a")));
        stream.Write(Greetd.Encode(new CancelSession()));
        stream.Position = 0;
        Assert.IsType<CreateSession>(Greetd.DecodeRequest((await Greetd.ReadFrameAsync(stream))!));
        Assert.IsType<CancelSession>(Greetd.DecodeRequest((await Greetd.ReadFrameAsync(stream))!));
        Assert.Null(await Greetd.ReadFrameAsync(stream));   // end of stream

        var huge = new MemoryStream(BitConverter.GetBytes(Greetd.MaxMessage + 1u));
        await Assert.ThrowsAsync<InvalidDataException>(() => Greetd.ReadFrameAsync(huge));
        var cut = new MemoryStream([.. BitConverter.GetBytes(10u), 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Greetd.ReadFrameAsync(cut));
    }
}

public class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vde-dm-" + Guid.NewGuid().ToString("N"));
    public AccountTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Users_are_real_accounts_with_a_shell_sorted_by_name()
    {
        var passwd = Path.Combine(_dir, "passwd");
        File.WriteAllText(passwd, string.Join('\n',
            "root:x:0:0:root:/root:/bin/bash",
            "daemon:x:1:1::/:/usr/bin/nologin",
            "zed:x:1001:1001:Zed Zimmer,,,:/home/zed:/bin/zsh",
            "alice:x:1000:1000:Alice A:/home/alice:/bin/fish",
            "ghost:x:1002:1002::/home/ghost:/usr/bin/nologin",
            "robot:x:1003:1003::/home/robot:/bin/false",
            "nobody:x:65534:65534:Nobody:/:/usr/bin/nologin",
            "# a comment", "broken line"));
        var users = UserDirectory.Load(passwd);
        Assert.Equal(new[] { "alice", "zed" }, users.Select(u => u.Name));
        Assert.Equal("Zed Zimmer", users[1].FullName);
        Assert.Equal("/home/alice", users[0].Home);
        Assert.Equal("root", UserDirectory.Find("root", passwd)!.Name);
        Assert.Null(UserDirectory.Find("nobody-here", passwd));
    }

    [Fact]
    public void Sessions_come_from_the_session_folders_vde_first_and_x11_ones_cannot_be_chosen()
    {
        var wl = Path.Combine(_dir, "usr", "share", "wayland-sessions"); var x = Path.Combine(_dir, "usr", "share", "xsessions");
        Directory.CreateDirectory(wl); Directory.CreateDirectory(x);
        File.WriteAllText(Path.Combine(wl, "plasma.desktop"), "[Desktop Entry]\nName=Plasma\nExec=startplasma-wayland\nDesktopNames=KDE\n");
        File.WriteAllText(Path.Combine(wl, "vde.desktop"), "[Desktop Entry]\nName=Vibe Desktop Environment\nComment=VDE\nExec=start-vde\nTryExec=start-vde\nDesktopNames=VDE\n");
        File.WriteAllText(Path.Combine(wl, "hidden.desktop"), "[Desktop Entry]\nName=Hidden\nExec=x\nHidden=true\n");
        File.WriteAllText(Path.Combine(wl, "missing.desktop"), "[Desktop Entry]\nName=Missing\nExec=nothing\nTryExec=nothing\n");
        File.WriteAllText(Path.Combine(x, "xfce.desktop"), "[Desktop Entry]\nName=Xfce\nExec=startxfce4\n");

        var sessions = SessionDirectory.Find([Path.Combine(_dir, "usr", "share")], exists: n => n != "nothing");

        Assert.Equal(new[] { "vde", "plasma", "xfce" }, sessions.Select(s => s.Id));
        Assert.Equal("VDE", sessions[0].DesktopNames);
        Assert.Equal(new[] { "start-vde" }, sessions[0].Command);
        Assert.Equal("vde", SessionDirectory.Choose(sessions, null)!.Id);
        Assert.Equal("plasma", SessionDirectory.Choose(sessions, "plasma")!.Id);
        Assert.Equal("vde", SessionDirectory.Choose(sessions, "xfce")!.Id);   // X11 is listed but not startable yet
    }
}

public class LoginManagerTests
{
    private sealed class FakeAuth : IAuthSession
    {
        public Queue<AuthEvent> Script { get; }
        public List<string?> Answers { get; } = [];
        public bool Closed;
        public FakeAuth(params AuthEvent[] script) => Script = new(script);
        public Task<AuthEvent> NextAsync(CancellationToken cancel = default) => Task.FromResult(Script.Dequeue());
        public void Respond(string? answer) => Answers.Add(answer);
        public IReadOnlyDictionary<string, string> OpenSession(SessionInfo info) => new Dictionary<string, string>();
        public void Close() => Closed = true;
        public void Dispose() => Close();
    }

    private sealed class FakeAuthenticator(Func<string, FakeAuth> make) : IAuthenticator
    {
        public List<string> Begun { get; } = [];
        public List<FakeAuth> Sessions { get; } = [];
        public IAuthSession Begin(string username) { Begun.Add(username); var s = make(username); Sessions.Add(s); return s; }
    }

    private static readonly LoginUser Alice = new("alice", 1000, 1000, "/home/alice", "/bin/fish", "Alice");

    private static LoginManager Manager(FakeAuthenticator auth, List<TimeSpan>? delays = null)
        => new(auth, n => n == "alice" ? Alice : null) { Delay = (t, _) => { delays?.Add(t); return Task.CompletedTask; } };

    [Fact]
    public async Task A_password_login_goes_prompt_answer_success_then_start_session_leaves_a_pending_session()
    {
        var auth = new FakeAuthenticator(_ => new FakeAuth(new Prompt(true, "Password: "), new Authenticated()));
        var m = Manager(auth);

        var first = Assert.IsType<AuthMessage>(await m.HandleAsync(new CreateSession("alice")));
        Assert.Equal(AuthMessageType.Secret, first.Type);
        Assert.IsType<Success>(await m.HandleAsync(new PostAuthMessageResponse("hunter2")));
        Assert.Equal(new[] { "hunter2" }, auth.Sessions[0].Answers);

        Assert.IsType<Success>(await m.HandleAsync(new StartSession(["start-vde"], ["FOO=bar"])));
        Assert.Equal("alice", m.Pending!.User.Name);
        Assert.Equal(new[] { "start-vde" }, m.Pending.Command);
        Assert.False(auth.Sessions[0].Closed);   // the pending session owns the PAM session now
    }

    [Fact]
    public async Task Info_and_error_notices_are_passed_on_and_a_failed_login_is_an_auth_error()
    {
        var auth = new FakeAuthenticator(_ => new FakeAuth(new Notice(false, "Touch the sensor"), new Failed("Authentication failure", true)));
        var m = Manager(auth);

        Assert.Equal(AuthMessageType.Info, Assert.IsType<AuthMessage>(await m.HandleAsync(new CreateSession("alice"))).Type);
        var failed = Assert.IsType<ErrorResponse>(await m.HandleAsync(new PostAuthMessageResponse(null)));
        Assert.Equal(ErrorType.AuthError, failed.Type);
        Assert.True(auth.Sessions[0].Closed);
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new PostAuthMessageResponse("x")));   // nothing is waiting any more
    }

    [Fact]
    public async Task Repeated_failures_slow_the_next_attempt_down_and_success_resets_it()
    {
        var delays = new List<TimeSpan>();
        var count = 0;
        var auth = new FakeAuthenticator(_ => ++count <= 3 ? new FakeAuth(new Failed("no", true)) : new FakeAuth(new Authenticated()));
        var m = Manager(auth, delays);

        for (var i = 0; i < 3; i++) await m.HandleAsync(new CreateSession("alice"));
        Assert.IsType<Success>(await m.HandleAsync(new CreateSession("alice")));

        Assert.Equal(new[] { 1.0, 2.0 , 4.0 }, delays.Select(d => d.TotalSeconds));   // before attempts 2, 3 and 4
        await m.HandleAsync(new CreateSession("alice"));
        Assert.Equal(3, delays.Count);   // the success cleared the count: no delay before the next one
    }

    [Fact]
    public async Task Start_session_needs_a_successful_login_a_command_and_sane_environment_entries()
    {
        var auth = new FakeAuthenticator(_ => new FakeAuth(new Authenticated()));
        var m = Manager(auth);
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession(["x"], [])));   // not logged in

        await m.HandleAsync(new CreateSession("alice"));
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession([], [])));
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession([""], [])));
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession(["x"], ["NOEQUALS"])));
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession(["x"], ["=v"])));
        Assert.IsType<ErrorResponse>(await m.HandleAsync(new StartSession(["x"], ["BAD NAME=v"])));
        Assert.Null(m.Pending);
        Assert.IsType<Success>(await m.HandleAsync(new StartSession(["x"], ["OK_1=v=w"])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    public async Task Odd_user_names_never_reach_PAM(string name)
    {
        var auth = new FakeAuthenticator(_ => new FakeAuth(new Authenticated()));
        Assert.IsType<ErrorResponse>(await Manager(auth).HandleAsync(new CreateSession(name)));
        Assert.Empty(auth.Begun);
    }

    [Fact]
    public async Task Cancel_and_a_new_session_close_the_old_attempt_and_an_unknown_account_cannot_start_a_session()
    {
        var auth = new FakeAuthenticator(_ => new FakeAuth(new Prompt(true, "Password: "), new Authenticated()));
        var m = Manager(auth);
        await m.HandleAsync(new CreateSession("alice"));
        Assert.IsType<Success>(await m.HandleAsync(new CancelSession()));
        Assert.True(auth.Sessions[0].Closed);

        await m.HandleAsync(new CreateSession("alice"));
        await m.HandleAsync(new CreateSession("alice"));   // a second create abandons the first
        Assert.True(auth.Sessions[1].Closed);

        // PAM knows "ldapuser" but the system does not: logging in works, starting the session does not
        var ldap = new FakeAuthenticator(_ => new FakeAuth(new Authenticated()));
        var m2 = Manager(ldap);
        Assert.IsType<Success>(await m2.HandleAsync(new CreateSession("ldapuser")));
        Assert.IsType<ErrorResponse>(await m2.HandleAsync(new StartSession(["x"], [])));
    }
}

public class PlanTests
{
    [Fact]
    public void A_session_runs_through_a_login_shell_with_the_users_environment_pams_and_the_greeters()
    {
        var alice = new LoginUser("alice", 1000, 1000, "/home/alice", "/bin/fish", "Alice");
        var pending = new PendingSession(alice, ["start-vde"], ["EXTRA=1", "LANG=de_DE.UTF-8"], new NoAuth());
        var plan = SessionPlans.Session(pending, new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["XDG_SESSION_ID"] = "c3" }, 2, "/usr/lib/vde/vterm-launch", "VDE");

        Assert.Equal(new[] { "/usr/lib/vde/vterm-launch", "/dev/tty2", "-C", "/home/alice", "--", "/bin/sh", "-lc", "exec \"$@\"", "vde-session", "start-vde" }, plan.Command);
        Assert.Equal("alice", plan.UserName);
        Assert.Equal("/home/alice", plan.Environment["HOME"]);
        Assert.Equal("alice", plan.Environment["LOGNAME"]);
        Assert.Equal("/run/user/1000", plan.Environment["XDG_RUNTIME_DIR"]);
        Assert.Equal("VDE", plan.Environment["XDG_CURRENT_DESKTOP"]);
        Assert.Equal("2", plan.Environment["XDG_VTNR"]);
        Assert.Equal("1", plan.Environment["EXTRA"]);
        Assert.Equal("de_DE.UTF-8", plan.Environment["LANG"]);   // what the greeter asked for wins over the default
        Assert.DoesNotContain("WAYLAND_DISPLAY", plan.Environment.Keys);
    }

    [Fact]
    public void The_greeter_gets_its_own_class_and_the_socket_under_both_names()
    {
        var plan = SessionPlans.Greeter("vdedm", "/var/lib/vdedm", ["vwm", "-d", "-s", "vdedm-greeter"], "/run/vdedm/greeter.sock", 1, "/usr/lib/vde/vterm-launch");
        Assert.Equal("greeter", plan.Environment["XDG_SESSION_CLASS"]);
        Assert.Equal("/run/vdedm/greeter.sock", plan.Environment["VDEDM_SOCK"]);
        Assert.Equal("/run/vdedm/greeter.sock", plan.Environment["GREETD_SOCK"]);
        Assert.Equal("vwm", plan.Command[5]);
        Assert.Equal("/dev/tty1", plan.Tty);
    }

    private sealed class NoAuth : IAuthSession
    {
        public Task<AuthEvent> NextAsync(CancellationToken cancel = default) => Task.FromResult<AuthEvent>(new Authenticated());
        public void Respond(string? answer) { }
        public IReadOnlyDictionary<string, string> OpenSession(SessionInfo info) => new Dictionary<string, string>();
        public void Close() { }
        public void Dispose() { }
    }
}
