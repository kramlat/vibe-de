using System.Diagnostics;

namespace VdeSession;

public sealed record ProcessSpec(string Name, IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Environment);

// A program the session started. Exited completes with its exit code (or -1 when it was killed by a signal).
public interface IRunningProcess
{
    Task<int> Exited { get; }
    void Terminate();   // ask politely (SIGTERM)
    void Kill();
}

// The session starts programs through this, so the rest can be tested without starting any.
public interface IProcessHost
{
    // Throws FileNotFoundException when the program cannot be found.
    IRunningProcess Start(ProcessSpec spec);
}

public sealed class SystemProcessHost : IProcessHost
{
    private readonly Func<string, string?> _lookup;

    // lookup finds a program by name (see ProgramFinder); the default searches PATH.
    public SystemProcessHost(Func<string, string?>? lookup = null) => _lookup = lookup ?? ProgramFinder.Default.Find;

    public IRunningProcess Start(ProcessSpec spec)
    {
        var program = _lookup(spec.Command[0]) ?? throw new FileNotFoundException($"Cannot find '{spec.Command[0]}'.", spec.Command[0]);
        var info = new ProcessStartInfo(program) { UseShellExecute = false };   // stdout and stderr stay the session's: they reach the journal or the log
        foreach (var arg in spec.Command.Skip(1))
            info.ArgumentList.Add(arg);
        foreach (var (k, v) in spec.Environment)
            info.Environment[k] = v;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start '{program}'.");
        return new Running(process);
    }

    private sealed class Running : IRunningProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Running(Process process)
        {
            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _exited.TrySetResult(process.ExitCode);
            if (process.HasExited) _exited.TrySetResult(process.ExitCode);
        }

        public Task<int> Exited => _exited.Task;
        public void Terminate() { try { if (!_process.HasExited) kill(_process.Id, 15); } catch (InvalidOperationException) { } }
        public void Kill() { try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { } }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}

// Finds programs: first where VDE installs its own (VDE_LIBEXEC, then /usr/lib/vde, then next to this program),
// then PATH.
public sealed class ProgramFinder
{
    public static ProgramFinder Default { get; } = new(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);

    private readonly Func<string, string?> _env;
    private readonly string _here;

    public ProgramFinder(Func<string, string?> env, string here)
    {
        _env = env;
        _here = here;
    }

    public string? Find(string name)
    {
        if (name.Contains('/'))
            return IsExecutable(name) ? name : null;
        var dirs = new List<string>();
        if (_env("VDE_LIBEXEC") is { Length: > 0 } libexec) dirs.Add(libexec);
        dirs.Add("/usr/lib/vde");
        dirs.Add(_here);
        dirs.AddRange((_env("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(IsExecutable);
    }

    private static bool IsExecutable(string path)
        => File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
