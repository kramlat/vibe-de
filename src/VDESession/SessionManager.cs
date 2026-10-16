using Vde.Freedesktop;
using Vibe.Registry;

namespace VdeSession;

// The VDE session: starts the compositor, waits for it to be ready, starts the shell programs and the user's
// autostart programs with the compositor's display in their environment, keeps them running, and takes
// everything down in the right order when the session ends.
public sealed class SessionManager
{
    public const int LoggedOut = 0, CompositorFailed = 1;

    private readonly IProcessHost _host;
    private readonly Func<string, string?> _env;
    private readonly IReadOnlyList<ComponentSpec> _components;
    private readonly IReadOnlyList<(string Name, IReadOnlyList<string> Command)> _autostart;
    private readonly string _envFile;
    private readonly TextWriter _log;
    private readonly List<Supervisor> _supervisors = new();
    private readonly List<IRunningProcess> _autostarted = new();
    private readonly TaskCompletionSource<int> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, string> _sessionEnv = new();

    // envFile: where the compositor reports WAYLAND_DISPLAY and DISPLAY (vwm writes it next to its control socket).
    public SessionManager(IProcessHost host, Func<string, string?> env, IReadOnlyList<ComponentSpec> components,
        IReadOnlyList<(string Name, IReadOnlyList<string> Command)> autostart, string envFile, TextWriter log)
    {
        _host = host;
        _env = env;
        _components = components;
        _autostart = autostart;
        _envFile = envFile;
        _log = log;
    }

    public TimeSpan StartupWait { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan XwaylandWait { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(3);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, c) => Task.Delay(t, c);
    // Tells systemd and D-Bus about the display (so services they start find it); best effort.
    public Action<IReadOnlyDictionary<string, string>>? Export { get; init; }
    public Backoff? Tuning { get; init; }

    public IReadOnlyDictionary<string, string> Environment => _sessionEnv;
    public IReadOnlyList<Supervisor> Components => _supervisors;
    public bool IsRunning { get; private set; }

    // Starts everything and returns when the session is over: logged out (0) or the compositor failed (1).
    public async Task<int> RunAsync(CancellationToken stop)
    {
        var baseEnv = SessionEnvironment.Base(_env);
        var inherited = Inherited();
        _sessionEnv = SessionEnvironment.For(inherited, baseEnv, null);
        try { File.Delete(_envFile); } catch (IOException) { }   // a stale file from a session that crashed would send us to a dead socket

        var compositor = _components.FirstOrDefault(c => c.Critical) ?? throw new InvalidOperationException("The session has no compositor.");
        var first = Add(compositor);
        first.Start();
        _log.WriteLine($"vde-session: started {compositor.Name}");

        var reported = await WaitForCompositorAsync(first, stop);
        if (reported is null)
        {
            _log.WriteLine("vde-session: the compositor did not come up" + (first.Problem is { } p ? ": " + p : ""));
            await ShutdownAsync();
            return CompositorFailed;
        }
        _sessionEnv = SessionEnvironment.For(inherited, baseEnv, reported);
        Export?.Invoke(_sessionEnv);
        IsRunning = true;

        foreach (var component in _components.Where(c => !c.Critical && c.Enabled))
        {
            var s = Add(component);
            s.Start();
            _log.WriteLine($"vde-session: started {component.Name}");
        }
        foreach (var (name, command) in _autostart)
            try
            {
                _autostarted.Add(_host.Start(new ProcessSpec(name, command, _sessionEnv)));
                _log.WriteLine($"vde-session: autostart {name}");
            }
            catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.WriteLine($"vde-session: autostart {name} skipped: {e.Message}");
            }

        using var registration = stop.Register(() => _end.TrySetResult(LoggedOut));
        var code = await _end.Task;
        IsRunning = false;
        await ShutdownAsync();
        return code;
    }

    // The control socket's "logout".
    public void Logout() => _end.TrySetResult(LoggedOut);

    public async Task RestartAsync(string name)
    {
        if (_supervisors.FirstOrDefault(s => s.Component.Name == name && !s.Component.Critical) is { } s)
            await s.RestartAsync(StopGrace);
    }

    private Supervisor Add(ComponentSpec component)
    {
        var s = new Supervisor(component, _host, () => new ProcessSpec(component.Name, component.Command, _sessionEnv))
        {
            Backoff = Tuning?.Delay ?? TimeSpan.FromSeconds(1),
            MaxRestarts = Tuning?.MaxRestarts ?? 5,
        };
        s.Ended += (_, _) =>
        {
            _log.WriteLine($"vde-session: {component.Name} ended ({s.Problem ?? "exit " + s.LastExitCode})");
            if (component.Critical) _end.TrySetResult(CompositorFailed);   // no compositor, no session
        };
        _supervisors.Add(s);
        return s;
    }

    // The compositor is ready when it has written its environment file; DISPLAY follows a moment later (XWayland).
    private async Task<IReadOnlyDictionary<string, string>?> WaitForCompositorAsync(Supervisor compositor, CancellationToken stop)
    {
        var deadline = DateTime.UtcNow + StartupWait;
        IReadOnlyDictionary<string, string>? seen = null;
        DateTime? displayDeadline = null;
        while (!stop.IsCancellationRequested && compositor.State != ComponentState.Failed && compositor.State != ComponentState.Exited)
        {
            if (TryRead() is { } vars && vars.ContainsKey("WAYLAND_DISPLAY"))
            {
                seen = vars;
                if (vars.ContainsKey("DISPLAY")) return vars;
                displayDeadline ??= DateTime.UtcNow + XwaylandWait;
                if (DateTime.UtcNow > displayDeadline) return vars;   // no XWayland: Wayland-only is still a session
            }
            else if (DateTime.UtcNow > deadline)
                return seen;
            await Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }
        return seen;
    }

    private IReadOnlyDictionary<string, string>? TryRead()
    {
        try { return File.Exists(_envFile) ? SessionEnvironment.ParseEnvFile(File.ReadAllText(_envFile)) : null; }
        catch (IOException) { return null; }
    }

    // Programs first (they are using the compositor), the compositor last.
    private async Task ShutdownAsync()
    {
        _log.WriteLine("vde-session: ending the session");
        foreach (var process in _autostarted) process.Terminate();
        foreach (var s in Enumerable.Reverse(_supervisors).ToList())
            await s.StopAsync(StopGrace);
        foreach (var process in _autostarted)
            if (await Task.WhenAny(process.Exited, Task.Delay(StopGrace)) != process.Exited)
                process.Kill();
        try { File.Delete(_envFile); } catch (IOException) { }
    }

    private IReadOnlyDictionary<string, string> Inherited()
    {
        var vars = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) vars[k] = v;
        return vars;
    }

    public string Status()
        => string.Join('\n', _supervisors.Select(s => $"{s.Component.Name,-14} {s.State,-10} restarts {s.Restarts}" + (s.Problem is { } p ? "  " + p : ""))) + "\n";
}

public sealed record Backoff(TimeSpan Delay, int MaxRestarts);
