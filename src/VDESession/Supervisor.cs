namespace VdeSession;

public enum ComponentState { Stopped, Running, Restarting, Failed, Exited }

// Keeps one component running as its policy says. A program that keeps dying is given up on (MaxRestarts within
// Window) rather than restarted forever.
public sealed class Supervisor
{
    private readonly IProcessHost _host;
    private readonly ProcessSpec _process;
    private readonly Func<ProcessSpec> _spec;
    private readonly List<DateTime> _starts = new();
    private readonly object _lock = new();
    private IRunningProcess? _running;
    private bool _stopping;
    private Task _loop = Task.CompletedTask;

    public Supervisor(ComponentSpec component, IProcessHost host, Func<ProcessSpec> spec)
    {
        Component = component;
        _host = host;
        _spec = spec;
        _process = spec();
    }

    public ComponentSpec Component { get; }
    public ComponentState State { get; private set; } = ComponentState.Stopped;
    public int Restarts { get; private set; }
    public int? LastExitCode { get; private set; }
    public string? Problem { get; private set; }
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxRestarts { get; init; } = 5;
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // Raised when the component ends for good: it exited and will not be restarted.
    public event EventHandler? Ended;

    public void Start()
    {
        lock (_lock)
        {
            _stopping = false;
            var first = TryStart();   // the first start happens here, so components start in the order they are started
            _loop = Task.Run(() => Loop(first));
        }
    }

    // Null when the program cannot be started (not installed: nothing will change by trying again).
    private IRunningProcess? TryStart()
    {
        try
        {
            return _host.Start(_spec());
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Problem = e.Message;
            State = ComponentState.Failed;
            return null;
        }
    }

    private async Task Loop(IRunningProcess? first)
    {
        var next = first;
        var firstRound = true;
        while (true)
        {
            var process = next ?? (firstRound ? null : TryStart());
            firstRound = false;
            next = null;
            if (process is null)
            {
                Ended?.Invoke(this, EventArgs.Empty);
                return;
            }
            lock (_lock) _running = process;
            State = ComponentState.Running;
            _starts.Add(Clock());
            var code = await process.Exited;
            LastExitCode = code;
            lock (_lock) _running = null;
            if (_stopping) { State = ComponentState.Stopped; return; }

            var again = Component.Restart switch
            {
                RestartPolicy.Always => true,
                RestartPolicy.OnFailure => code != 0,
                _ => false,
            } && Component.PermanentFailureCodes?.Contains(code) != true;
            if (again)
            {
                var recent = _starts.Count(s => Clock() - s < Window);
                if (recent > MaxRestarts)
                {
                    Problem = $"stopped {recent} times in {Window.TotalSeconds:0} s; giving up";
                    State = ComponentState.Failed;
                    Ended?.Invoke(this, EventArgs.Empty);
                    return;
                }
                State = ComponentState.Restarting;
                Restarts++;
                await Task.Delay(Backoff);
                if (_stopping) { State = ComponentState.Stopped; return; }
                continue;
            }
            State = ComponentState.Exited;
            Ended?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    // SIGTERM, then SIGKILL if it has not gone after `grace`.
    public async Task StopAsync(TimeSpan grace)
    {
        IRunningProcess? process;
        lock (_lock)
        {
            _stopping = true;
            process = _running;
        }
        if (process is not null)
        {
            process.Terminate();
            if (await Task.WhenAny(process.Exited, Task.Delay(grace)) != process.Exited)
            {
                process.Kill();
                await process.Exited;
            }
        }
        await _loop;
        State = ComponentState.Stopped;
    }

    // Stop it and start it again (the control command "restart").
    public async Task RestartAsync(TimeSpan grace)
    {
        await StopAsync(grace);
        _starts.Clear();
        Start();
    }
}
