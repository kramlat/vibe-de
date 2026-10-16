using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vde.Shell;

namespace Vde.Compositor;

// vwm wants the titlebar of a window drawn (see native/vwm/vwm.h for the sizes).
public sealed record ChromeRequest(uint Id, int Width, bool Active, bool Maximized, string Hover, bool Pressed, string Title, string AppId);

// vwm opened a window's menu and wants it drawn: `Enabled` has one bit per row, `Hover` is the row under the pointer or -1.
public sealed record MenuRequest(uint Id, uint Enabled, int Hover, bool Maximized);

// The shell's connection to vwm (native/vwm, docs/vwm.md): it reports the windows and takes commands for them.
// This is the taskbar's window source. Lines in are JSON events, lines out are plain commands.
public sealed class VwmClient : IWindowSource, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, WindowInfo> _windows = new();
    private readonly List<uint> _order = new();   // the order windows first appeared in, so taskbar buttons do not shuffle
    private Socket? _socket;
    private CancellationTokenSource? _stop;

    public event EventHandler? Changed;
    // vwm asks for a titlebar image. Raised on the reading thread.
    public event EventHandler<ChromeRequest>? ChromeRequested;
    public event EventHandler<MenuRequest>? MenuRequested;
    // A window went away: forget anything kept for it.
    public event EventHandler<uint>? WindowClosed;
    // vwm went away (or never was there): the window list is cleared.
    public event EventHandler? Disconnected;

    public static string DefaultSocketPath
        => Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "vde", "vwm.sock");

    public bool IsConnected => _socket is { Connected: true };

    public IReadOnlyList<WindowInfo> Windows
    {
        get
        {
            lock (_lock)
                return _order.Select(id => _windows[id]).ToList();
        }
    }

    // False when there is no compositor to talk to.
    public bool Connect(string? path = null)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path ?? DefaultSocketPath));
        }
        catch (SocketException)
        {
            socket.Dispose();
            return false;
        }
        _socket = socket;
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(socket, _stop.Token));
        return true;
    }

    public void Activate(string id) => Send("activate", id);
    public void Minimize(string id) => Send("minimize", id);
    public void Close(string id) => Send("close", id);
    public void Maximize(string id) => Send("maximize", id);
    public void Unmaximize(string id) => Send("unmaximize", id);

    public void Dispose()
    {
        _stop?.Cancel();
        _socket?.Dispose();
        _socket = null;
    }

    // Sends the drawn titlebar of window `id`: premultiplied BGRA, width*height*4 bytes (height is vwm's TITLEBAR_H).
    public void SendChrome(uint id, int width, int height, ReadOnlySpan<byte> bgra) => SendImage("chrome", id, width, height, bgra);

    // The drawn window menu (WindowMenuView's size).
    public void SendMenu(uint id, int width, int height, ReadOnlySpan<byte> bgra) => SendImage("menu", id, width, height, bgra);

    // What double-clicking a titlebar does: maximize (default), minimize, close or none.
    public void SetTitlebarDoubleClick(string action)
    {
        if (action is not ("maximize" or "minimize" or "close" or "none") || _socket is not { } socket) return;
        try { lock (_lock) socket.Send(Encoding.ASCII.GetBytes($"titlebar-doubleclick {action}\n")); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void SendImage(string verb, uint id, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (_socket is not { } socket || bgra.Length != width * height * 4) return;
        var header = Encoding.ASCII.GetBytes($"{verb} {id} {width} {height}\n");
        var message = new byte[header.Length + bgra.Length];
        header.CopyTo(message, 0);
        bgra.CopyTo(message.AsSpan(header.Length));
        try
        {
            lock (_lock)   // one message at a time: the image must follow its header unbroken
            {
                var sent = 0;
                while (sent < message.Length)
                    sent += socket.Send(message.AsSpan(sent));
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void Send(string verb, string id)
    {
        if (!uint.TryParse(id, out _) || _socket is not { } socket) return;   // ids are numbers; nothing else goes on the wire
        try
        {
            socket.Send(Encoding.ASCII.GetBytes($"{verb} {id}\n"));
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadLoop(Socket socket, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        var line = new List<byte>();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cancel);
                if (n == 0) break;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') { line.Add(buffer[i]); continue; }
                    Handle(Encoding.UTF8.GetString(line.ToArray()));
                    line.Clear();
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }

        lock (_lock)
        {
            _windows.Clear();
            _order.Clear();
        }
        if (!cancel.IsCancellationRequested)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // Public so a test can feed it lines without a socket.
    public void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = root.GetProperty("event").GetString();
            var id = root.GetProperty("id").GetUInt32();
            lock (_lock)
            {
                if (ev == "chrome")
                {
                    var request = new ChromeRequest(id, root.GetProperty("width").GetInt32(), Bool(root, "active"), Bool(root, "maximized"),
                        Str(root, "hover"), Bool(root, "pressed"), Str(root, "title"), Str(root, "app_id"));
                    ChromeRequested?.Invoke(this, request);
                    return;
                }
                if (ev == "menu")
                {
                    MenuRequested?.Invoke(this, new MenuRequest(id, root.GetProperty("enabled").GetUInt32(), root.GetProperty("hover").GetInt32(), Bool(root, "maximized")));
                    return;
                }
                if (ev == "closed")
                {
                    _windows.Remove(id);
                    _order.Remove(id);
                    WindowClosed?.Invoke(this, id);
                }
                else if (ev == "window")
                {
                    var info = new WindowInfo(id.ToString(), Str(root, "title"), Str(root, "app_id"),
                        Bool(root, "active"), Bool(root, "minimized"));
                    if (!_windows.ContainsKey(id)) _order.Add(id);
                    _windows[id] = info;
                }
                else return;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // a line we do not understand is skipped; the compositor and shell may be different versions
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
