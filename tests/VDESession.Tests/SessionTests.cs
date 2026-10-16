using Vde.Freedesktop;
using Vibe.Registry;
using VdeSession;

namespace VdeSession.Tests;

public class SessionTests
{
    private sealed class FakeProcess : IRunningProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessSpec Spec { get; }
        public bool Terminated, Killed;
        public bool IgnoreTerminate;
        public FakeProcess(ProcessSpec spec) => Spec = spec;
        public Task<int> Exited => _exit.Task;
        public void Terminate() { Terminated = true; if (!IgnoreTerminate) _exit.TrySetResult(0); }
        public void Kill() { Killed = true; _exit.TrySetResult(-1); }
        public void Exit(int code) => _exit.TrySetResult(code);
    }

    private sealed class FakeHost : IProcessHost
    {
        public List<FakeProcess> Started { get; } = [];
        public HashSet<string> Missing { get; } = [];
        public List<string> Order { get; } = [];
        public Action<FakeProcess>? OnStart;
        public IRunningProcess Start(ProcessSpec spec)
        {
            if (Missing.Contains(spec.Command[0])) throw new FileNotFoundException("no " + spec.Command[0], spec.Command[0]);
            var p = new FakeProcess(spec);
            lock (Started) { Started.Add(p); Order.Add(spec.Name); }
            OnStart?.Invoke(p);
            return p;
        }
        public FakeProcess Get(string name) { lock (Started) return Started.Last(p => p.Spec.Name == name); }
        public int Count(string name) { lock (Started) return Started.Count(p => p.Spec.Name == name); }
    }

    private sealed class Dir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-session-" + Guid.NewGuid().ToString("N"));
        public Dir() => Directory.CreateDirectory(Root);
        public string EnvFile => Path.Combine(Root, "vwm.env");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not reached"); await Task.Delay(10); }
    }

    // vwm "comes up" the moment it starts: its env file appears
    private static FakeHost Host(Dir d, bool xwayland = true)
    {
        var host = new FakeHost();
        host.OnStart = p => { if (p.Spec.Name == "vwm") File.WriteAllText(d.EnvFile, "WAYLAND_DISPLAY=wayland-vde\n" + (xwayland ? "DISPLAY=:7\n" : "")); };
        return host;
    }

    private static SessionManager Manager(FakeHost host, Dir d, IReadOnlyList<ComponentSpec>? components = null, IReadOnlyList<(string, IReadOnlyList<string>)>? autostart = null, TextWriter? log = null)
        => new(host, _ => null, components ?? SessionComponents.Load(null), autostart ?? [], d.EnvFile, log ?? TextWriter.Null)
        {
            StopGrace = TimeSpan.FromMilliseconds(200), Tuning = new Backoff(TimeSpan.FromMilliseconds(10), 3),
        };

    [Fact]
    public async Task The_compositor_starts_first_and_the_rest_get_its_display_in_their_environment()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d);
        using var stop = new CancellationTokenSource();

        var run = session.RunAsync(stop.Token);
        await Until(() => host.Count("notifications") == 1);

        Assert.Equal(new[] { "vwm", "chrome", "desktop", "taskbar", "notifications" }, host.Order);
        foreach (var name in new[] { "chrome", "desktop", "taskbar", "notifications" })
        {
            var env = host.Get(name).Spec.Environment;
            Assert.Equal("wayland-vde", env["WAYLAND_DISPLAY"]);
            Assert.Equal(":7", env["DISPLAY"]);
            Assert.Equal("VDE", env["XDG_CURRENT_DESKTOP"]);
            Assert.Equal("wayland", env["XDG_SESSION_TYPE"]);
        }
        Assert.Equal("wayland-vde", host.Get("vwm").Spec.Command[2]);   // vwm -w wayland-vde

        stop.Cancel();
        Assert.Equal(SessionManager.LoggedOut, await run);
    }

    [Fact]
    public async Task Without_XWayland_the_session_still_starts_with_only_a_Wayland_display()
    {
        using var d = new Dir();
        var host = Host(d, xwayland: false);
        var session = Manager(host, d);
        var short_ = new SessionManager(host, _ => null, SessionComponents.Load(null), [], d.EnvFile, TextWriter.Null)
        { XwaylandWait = TimeSpan.FromMilliseconds(100), StopGrace = TimeSpan.FromMilliseconds(200) };
        using var stop = new CancellationTokenSource();

        var run = short_.RunAsync(stop.Token);
        await Until(() => host.Count("taskbar") == 1);

        Assert.False(host.Get("taskbar").Spec.Environment.ContainsKey("DISPLAY"));
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Logging_out_stops_the_programs_before_the_compositor_and_removes_the_env_file()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("notifications") == 1);

        session.Logout();
        Assert.Equal(SessionManager.LoggedOut, await run);

        Assert.All(host.Started, p => Assert.True(p.Terminated));
        Assert.False(File.Exists(d.EnvFile));
    }

    [Fact]
    public async Task A_program_that_ignores_SIGTERM_is_killed_after_the_grace_period()
    {
        using var d = new Dir();
        var host = Host(d);
        host.OnStart += p => { if (p.Spec.Name == "taskbar") p.IgnoreTerminate = true; };
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("notifications") == 1);

        session.Logout();
        await run;

        Assert.True(host.Get("taskbar").Killed);
        Assert.False(host.Get("desktop").Killed);
    }

    [Fact]
    public async Task A_crashed_shell_program_is_restarted_but_one_that_keeps_crashing_is_given_up_on()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("taskbar") == 1);

        host.Get("taskbar").Exit(1);
        await Until(() => host.Count("taskbar") == 2);   // back again

        for (var i = 0; i < 10 && session.Components.First(c => c.Component.Name == "taskbar").State != ComponentState.Failed; i++)
        {
            var n = host.Count("taskbar");
            host.Get("taskbar").Exit(1);
            await Until(() => host.Count("taskbar") > n || session.Components.First(c => c.Component.Name == "taskbar").State == ComponentState.Failed);
        }
        Assert.Equal(ComponentState.Failed, session.Components.First(c => c.Component.Name == "taskbar").State);
        Assert.Contains("giving up", session.Components.First(c => c.Component.Name == "taskbar").Problem);
        Assert.Equal(ComponentState.Running, session.Components.First(c => c.Component.Name == "desktop").State);   // the others carry on

        session.Logout();
        await run;
    }

    [Fact]
    public async Task The_compositor_ending_ends_the_session_with_a_failure_status()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("notifications") == 1);

        host.Get("vwm").Exit(1);

        Assert.Equal(SessionManager.CompositorFailed, await run);
        Assert.True(host.Get("taskbar").Terminated);
    }

    [Fact]
    public async Task A_notification_daemon_that_cannot_get_its_name_is_not_restarted()
    {
        using var d = new Dir();
        var host = Host(d);
        host.OnStart += p => { if (p.Spec.Name == "notifications") p.Exit(2); };   // another daemon owns org.freedesktop.Notifications
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => session.Components.Any(c => c.Component.Name == "notifications" && c.State == ComponentState.Exited));
        await Task.Delay(100);

        Assert.Equal(1, host.Count("notifications"));
        session.Logout();
        await run;
    }

    [Fact]
    public async Task A_missing_compositor_fails_the_session_and_a_missing_shell_program_does_not()
    {
        using var d = new Dir();
        var noCompositor = Host(d);
        noCompositor.Missing.Add("vwm");
        Assert.Equal(SessionManager.CompositorFailed, await Manager(noCompositor, d).RunAsync(CancellationToken.None));

        var noTaskbar = Host(d);
        noTaskbar.Missing.Add("vexplore");
        var session = Manager(noTaskbar, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => noTaskbar.Count("notifications") == 1);
        Assert.Equal(ComponentState.Failed, session.Components.First(c => c.Component.Name == "taskbar").State);
        session.Logout();
        await run;
    }

    [Fact]
    public async Task Autostart_programs_get_the_same_environment_and_are_stopped_at_logout()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d, autostart: [("Clipboard manager", ["klipper"]), ("Missing", ["nope"])]);
        host.Missing.Add("nope");
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("Clipboard manager") == 1);

        Assert.Equal("wayland-vde", host.Get("Clipboard manager").Spec.Environment["WAYLAND_DISPLAY"]);
        session.Logout();
        await run;
        Assert.True(host.Get("Clipboard manager").Terminated);
    }

    [Fact]
    public void The_registry_can_disable_a_program_or_replace_its_command_but_not_remove_the_compositor()
    {
        var hive = new Hive("HKCU");
        hive.SetValue(SessionComponents.KeyPath + @"\taskbar", "Enabled", RegistryValueKind.DWord, 0u);
        hive.SetValue(SessionComponents.KeyPath + @"\desktop", "Command", RegistryValueKind.String, "my-desktop --fancy");
        hive.SetValue(SessionComponents.KeyPath + @"\vwm", "Enabled", RegistryValueKind.DWord, 0u);
        hive.SetValue(SessionComponents.KeyPath + @"\vwm", "Command", RegistryValueKind.String, "evil");

        var components = SessionComponents.Load(hive);

        Assert.False(components.Single(c => c.Name == "taskbar").Enabled);
        Assert.Equal(new[] { "my-desktop", "--fancy" }, components.Single(c => c.Name == "desktop").Command);
        Assert.Equal("vwm", components.Single(c => c.Name == "vwm").Command[0]);   // the compositor stays
    }

    [Fact]
    public void Autostart_follows_the_desktop_entry_rules_and_the_users_file_overrides_the_systems()
    {
        using var d = new Dir();
        var user = Path.Combine(d.Root, "user", "autostart");
        var system = Path.Combine(d.Root, "etc", "autostart");
        Directory.CreateDirectory(user); Directory.CreateDirectory(system);
        File.WriteAllText(Path.Combine(system, "a.desktop"), "[Desktop Entry]\nType=Application\nName=A\nExec=a-system\n");
        File.WriteAllText(Path.Combine(user, "a.desktop"), "[Desktop Entry]\nType=Application\nName=A\nExec=a-user\n");
        File.WriteAllText(Path.Combine(system, "off.desktop"), "[Desktop Entry]\nType=Application\nName=Off\nExec=off\n");
        File.WriteAllText(Path.Combine(user, "off.desktop"), "[Desktop Entry]\nType=Application\nName=Off\nExec=off\nHidden=true\n");   // the user switched it off
        File.WriteAllText(Path.Combine(system, "gnome.desktop"), "[Desktop Entry]\nType=Application\nName=Gnome\nExec=g\nOnlyShowIn=GNOME;\n");
        File.WriteAllText(Path.Combine(system, "dead.desktop"), "[Desktop Entry]\nType=Application\nName=Dead\nExec=dead\nTryExec=dead\n");
        File.WriteAllText(Path.Combine(system, "vde.desktop"), "[Desktop Entry]\nType=Application\nName=Vde only\nExec=v\nOnlyShowIn=VDE;\n");
        var envVars = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = Path.Combine(d.Root, "user"), ["XDG_CONFIG_DIRS"] = Path.Combine(d.Root, "etc"), ["HOME"] = d.Root };
        var xdg = new XdgDirectories(k => envVars.GetValueOrDefault(k));

        var found = Autostart.Find(xdg, ["VDE"], exists: name => name != "dead");

        Assert.Equal(new[] { "a-user", "v" }, found.Select(f => f.Command[0]));
    }

    [Fact]
    public void The_compositors_env_file_is_parsed_and_unrelated_variables_are_ignored()
    {
        var vars = SessionEnvironment.ParseEnvFile("WAYLAND_DISPLAY=wayland-vde\nDISPLAY=:3\nPATH=/evil\nGARBAGE\nDISPLAY=\n");
        Assert.Equal(2, vars.Count);
        Assert.Equal(":3", vars["DISPLAY"]);   // a later empty value does not erase it
        Assert.DoesNotContain("PATH", vars.Keys);
    }

    [Fact]
    public void The_session_drops_the_display_of_whatever_it_was_started_from()
    {
        var inherited = new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-0", ["DISPLAY"] = ":0", ["HOME"] = "/home/u" };
        var merged = SessionEnvironment.For(inherited, SessionEnvironment.Base(_ => null), null);
        Assert.False(merged.ContainsKey("WAYLAND_DISPLAY"));
        Assert.False(merged.ContainsKey("DISPLAY"));
        Assert.Equal("/home/u", merged["HOME"]);
        Assert.Equal("VDE", merged["XDG_CURRENT_DESKTOP"]);
    }

    [Fact]
    public async Task The_control_socket_reports_status_restarts_a_component_and_logs_out()
    {
        using var d = new Dir();
        var host = Host(d);
        var session = Manager(host, d);
        var run = session.RunAsync(CancellationToken.None);
        await Until(() => host.Count("notifications") == 1);
        var path = Path.Combine(d.Root, "session.sock");
        using var control = new SessionControl(path, session);

        Assert.True(SessionControl.IsAlive(path));
        var status = SessionControl.Ask(path, "status")!;
        Assert.Contains("vwm", status);
        Assert.Contains("taskbar", status);
        Assert.Contains("Running", status);

        Assert.Contains("restarted taskbar", SessionControl.Ask(path, "restart taskbar"));
        Assert.Equal(2, host.Count("taskbar"));
        Assert.Contains("no such component", SessionControl.Ask(path, "restart vwm"));   // the compositor is not restartable that way

        Assert.Contains("ending", SessionControl.Ask(path, "logout"));
        Assert.Equal(SessionManager.LoggedOut, await run);
    }

    [Fact]
    public void Programs_are_found_in_VDE_LIBEXEC_before_PATH()
    {
        using var d = new Dir();
        var lib = Path.Combine(d.Root, "lib"); var bin = Path.Combine(d.Root, "bin");
        Directory.CreateDirectory(lib); Directory.CreateDirectory(bin);
        foreach (var dir in new[] { lib, bin })
        {
            var file = Path.Combine(dir, "vexplore");
            File.WriteAllText(file, "#!/bin/sh\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var env = new Dictionary<string, string> { ["VDE_LIBEXEC"] = lib, ["PATH"] = bin };
        var finder = new ProgramFinder(k => env.GetValueOrDefault(k), "/nonexistent");

        Assert.Equal(Path.Combine(lib, "vexplore"), finder.Find("vexplore"));
        Assert.Null(finder.Find("nothing-here"));
    }
}
