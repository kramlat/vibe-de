using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vde.Terminal;

// A pseudo-terminal with a program on the other end. The program is started through vterm-launch (native/vterm),
// which makes the pty its controlling terminal: a .NET process cannot fork safely, so it does not.
public sealed class Pty : IDisposable
{
    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ref WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr read(int fd, byte[] buffer, IntPtr count);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr write(int fd, byte[] buffer, IntPtr count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    private const int ORdwr = 2, ONoctty = 0x100;
    private const ulong TiocSwinsz = 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    private readonly int _master;
    private readonly Process _child;
    private int _closed;

    private Pty(int master, Process child)
    {
        _master = master;
        _child = child;
    }

    public int ProcessId => _child.Id;
    // Completes when the program has ended, with its exit status.
    public Task<int> Exited { get; private set; } = Task.FromResult(0);

    // Throws FileNotFoundException when vterm-launch is not installed. `command` is the program and its arguments.
    public static Pty Start(IReadOnlyList<string> command, int columns, int rows, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null, string? launcher = null)
    {
        launcher ??= FindLauncher() ?? throw new FileNotFoundException("vterm-launch is not installed (build it with native/vterm/build.sh).");
        var master = posix_openpt(ORdwr | ONoctty);
        if (master < 0) throw new IOException("Cannot open a pseudo-terminal: errno " + Marshal.GetLastWin32Error());
        if (grantpt(master) != 0 || unlockpt(master) != 0) { close(master); throw new IOException("Cannot set up the pseudo-terminal."); }
        var slave = Marshal.PtrToStringAnsi(ptsname(master)) ?? throw new IOException("Cannot name the pseudo-terminal.");
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)columns };
        ioctl(master, TiocSwinsz, ref size);

        var info = new ProcessStartInfo(launcher) { UseShellExecute = false };
        info.ArgumentList.Add(slave);
        if (workingDirectory is not null) { info.ArgumentList.Add("-C"); info.ArgumentList.Add(workingDirectory); }
        info.ArgumentList.Add("--");
        foreach (var arg in command) info.ArgumentList.Add(arg);
        info.Environment["TERM"] = "xterm-256color";
        info.Environment["COLORTERM"] = "truecolor";
        if (environment is not null)
            foreach (var (k, v) in environment) info.Environment[k] = v;

        Process child;
        try { child = Process.Start(info) ?? throw new IOException("Cannot start the program."); }
        catch { close(master); throw; }
        var pty = new Pty(master, child);
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        child.EnableRaisingEvents = true;
        child.Exited += (_, _) => done.TrySetResult(child.ExitCode);
        if (child.HasExited) done.TrySetResult(child.ExitCode);
        pty.Exited = done.Task;
        return pty;
    }

    // Blocks until the program writes something; 0 when it has closed the terminal.
    public int Read(byte[] buffer)
    {
        var n = (long)read(_master, buffer, buffer.Length);
        if (n < 0) return 0;   // EIO: the other end is gone
        return (int)n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        var offset = 0;
        while (offset < bytes.Length && _closed == 0)
        {
            var chunk = bytes.AsSpan(offset).ToArray();
            var n = (long)write(_master, chunk, chunk.Length);
            if (n <= 0) return;
            offset += (int)n;
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_closed != 0) return;
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)columns };
        ioctl(_master, TiocSwinsz, ref size);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        close(_master);   // the program gets SIGHUP, as it would when a terminal window is closed
        try { if (!_child.HasExited) _child.Kill(); } catch (InvalidOperationException) { }
        _child.Dispose();
    }

    public static string? FindLauncher()
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("VDE_LIBEXEC") is { Length: > 0 } libexec) dirs.Add(libexec);
        dirs.Add("/usr/lib/vde");
        dirs.Add(AppContext.BaseDirectory);
        // a source checkout: walk up to native/build/vterm
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            dirs.Add(Path.Combine(d.FullName, "native", "build", "vterm"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => Path.Combine(d, "vterm-launch")).FirstOrDefault(File.Exists);
    }
}
