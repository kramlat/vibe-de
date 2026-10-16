using System.Net.Sockets;
using System.Text;

namespace VdeSession;

// $XDG_RUNTIME_DIR/vde/session.sock: `vde-session --logout` and `--status` talk to the running session through it.
// One command per line in; the answer comes back and the connection closes.
public sealed class SessionControl : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly SessionManager _session;
    private readonly CancellationTokenSource _stop = new();

    public static string DefaultPath(Func<string, string?> env) => Path.Combine(env("XDG_RUNTIME_DIR") ?? "/tmp", "vde", "session.sock");

    // True when a session is already answering on the socket (so a second one must not start).
    public static bool IsAlive(string path)
    {
        try { return Ask(path, "status") is not null; }
        catch (Exception e) when (e is SocketException or IOException) { return false; }
    }

    public SessionControl(string path, SessionManager session)
    {
        _path = path;
        _session = session;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // only the session's own user
        _listener.Listen(4);
        _ = Task.Run(Serve);
    }

    public static string? Ask(string path, string command)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 5000 };
        s.Connect(new UnixDomainSocketEndPoint(path));
        s.Send(Encoding.UTF8.GetBytes(command + "\n"));
        s.Shutdown(SocketShutdown.Send);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        int n;
        while ((n = s.Receive(buffer)) > 0)
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        return text.ToString();
    }

    private async Task Serve()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(_stop.Token);
                _ = Task.Run(() => Handle(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task Handle(Socket client)
    {
        using (client)
        {
            try
            {
                var buffer = new byte[256];
                var n = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                var line = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                var reply = await Run(line);
                await client.SendAsync(Encoding.UTF8.GetBytes(reply), SocketFlags.None, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private async Task<string> Run(string line)
    {
        var words = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (words)
        {
            case ["status"]: return _session.Status();
            case ["logout"]: _session.Logout(); return "ending the session\n";
            case ["restart", var name]:
                if (!_session.Components.Any(c => c.Component.Name == name && !c.Component.Critical)) return $"no such component: {name}\n";
                await _session.RestartAsync(name);
                return $"restarted {name}\n";
            default: return "commands: status, logout, restart NAME\n";
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
