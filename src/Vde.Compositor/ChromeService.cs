using System.Collections.Concurrent;

namespace Vde.Compositor;

// Answers vwm's requests for window titlebars. Requests arrive on the connection's thread; they are drawn on the
// thread that calls Pump or Run, which must be the Avalonia UI thread.
public sealed class ChromeService
{
    private readonly VwmClient _client;
    private readonly ChromeRenderer _renderer;
    private readonly ConcurrentDictionary<uint, ChromeRequest> _pending = new();   // newest request per window: older ones are stale
    private readonly ConcurrentDictionary<uint, ChromeRequest> _last = new();
    private readonly ConcurrentDictionary<uint, MenuRequest> _menus = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _gone;

    public ChromeService(VwmClient client, ChromeRenderer renderer)
    {
        _client = client;
        _renderer = renderer;
        client.ChromeRequested += (_, r) => Enqueue(r);
        client.MenuRequested += (_, m) => { _menus[m.Id] = m; _signal.Release(); };
        client.WindowClosed += (_, id) => { _pending.TryRemove(id, out ChromeRequest? _); _last.TryRemove(id, out ChromeRequest? _); };
        client.Disconnected += (_, _) => { _gone = true; _signal.Release(); };
    }

    public bool Disconnected => _gone;

    // The theme changed: every window's titlebar is drawn again with the new colours.
    public void RedrawAll()
    {
        foreach (var r in _last.Values) Enqueue(r);
    }

    private void Enqueue(ChromeRequest r)
    {
        _pending[r.Id] = r;
        _signal.Release();
    }

    // Draws and sends what is waiting; returns how many titlebars were sent. Waits up to `wait` for the first request.
    public int Pump(TimeSpan wait)
    {
        if (_pending.IsEmpty && _menus.IsEmpty && !_signal.Wait(wait))
            return 0;
        var sent = 0;
        foreach (var id in _menus.Keys.ToList())
        {
            if (!_menus.TryRemove(id, out var menu)) continue;
            try { _client.SendMenu(menu.Id, Vde.Shell.Controls.WindowMenuView.Width_, Vde.Shell.Controls.WindowMenuView.Height_, _renderer.RenderMenu(menu)); sent++; }
            catch (Exception e) { Console.Error.WriteLine($"vexplore: cannot draw the menu of window {id}: {e.Message}"); }
        }
        foreach (var id in _pending.Keys.ToList())
        {
            if (!_pending.TryRemove(id, out var request)) continue;
            _last[id] = request;
            var pixels = _renderer.Render(request);
            _client.SendChrome(request.Id, Math.Max(request.Width, 1), Vde.Shell.Controls.WindowChromeView.BarHeight, pixels);
            sent++;
        }
        return sent;
    }

    // Until vwm goes away.
    public void Run(CancellationToken cancel)
    {
        while (!_gone && !cancel.IsCancellationRequested)
            Pump(TimeSpan.FromMilliseconds(250));
    }
}
