using System.Text;

namespace Vde.Terminal;

// A terminal: a program on a pty, and the emulator showing what it writes.
public sealed class TerminalSession : IDisposable
{
    private readonly Pty _pty;
    private readonly Thread _reader;
    private volatile bool _disposed;

    public TerminalSession(Pty pty, Emulator emulator)
    {
        _pty = pty;
        Emulator = emulator;
        emulator.Respond += bytes => _pty.Write(bytes);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "terminal reader" };
        _reader.Start();
        _ = _pty.Exited.ContinueWith(t => { ExitCode = t.Result; });
    }

    public static TerminalSession Start(IReadOnlyList<string> command, int columns, int rows, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null, string? launcher = null)
    {
        var pty = Pty.Start(command, columns, rows, workingDirectory, environment, launcher);
        return new TerminalSession(pty, new Emulator(columns, rows));
    }

    public Emulator Emulator { get; }
    public int? ExitCode { get; private set; }
    public Task<int> Exited => _pty.Exited;
    public int ProcessId => _pty.ProcessId;

    // Raised (on the reader thread) after output was processed.
    public event Action? Output;
    // Raised when the output ended: the program closed its terminal.
    public event Action? Ended;

    public void Send(ReadOnlySpan<byte> bytes) => _pty.Write(bytes);
    public void Send(string text) => _pty.Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int columns, int rows)
    {
        lock (Emulator.SyncRoot) Emulator.Resize(columns, rows);
        _pty.Resize(columns, rows);
    }

    private void ReadLoop()
    {
        var buffer = new byte[16384];
        while (!_disposed)
        {
            var n = _pty.Read(buffer);
            if (n <= 0) break;
            lock (Emulator.SyncRoot) Emulator.Feed(buffer.AsSpan(0, n));
            Output?.Invoke();
        }
        Ended?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _pty.Dispose();
    }
}
