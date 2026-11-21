using Tmds.DBus.Protocol;

namespace Vde.Shell.Tray;

// An app that offers a widget, as it lives on the session bus: the interface org.vde.Widget1 at the object path the app's .desktop file names
//   method GetCard() -> s          the card as JSON (see WidgetCard)
//   method Activate(s action)      a button or list entry of the card was pressed
//   signal Changed()               the card is different: ask again
// The shell calls the app, so the app (a Flatpak, say) needs only to own its bus name: --own-name=org.example.Widget. If the app ships a
// D-Bus service file for that name, calling it starts it.
public sealed class WidgetAppClient(DBusConnection connection, string service, string path) : IDisposable
{
    public const string Interface = "org.vde.Widget1";

    private IDisposable? _watch;

    public string Service { get; } = service;
    public string Path { get; } = path;

    // The card changed on the app's side. Raised on a bus thread.
    public event Action? Changed;

    public static bool IsValid(string service, string path) => StatusNotifierItemClient.IsValidBusName(service) && StatusNotifierItemClient.IsValidObjectPath(path);

    public async Task<string?> GetCardAsync()
    {
        if (!IsValid(Service, Path)) return null;
        Task<string> call;
        using (var w = connection.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Service, Path, Interface, "GetCard");
            call = connection.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadString(), null);
        }
        try { return await call; }
        catch (DBusExceptionBase) { return null; }   // not installed, not running and not activatable, or no such widget
    }

    public Task ActivateAsync(string action)
    {
        if (!IsValid(Service, Path) || action.Length == 0) return Task.CompletedTask;
        using var w = connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, "Activate", "s");
        w.WriteString(action);
        return connection.CallMethodAsync(w.CreateMessage());
    }

    // Starts following the app's Changed signal.
    public async Task WatchAsync()
    {
        if (!IsValid(Service, Path)) return;
        _watch = await connection.WatchSignalAsync(Service, Path, Interface, "Changed", (Message m, object? _) => 0,
            (Notification<int> _) => Changed?.Invoke(), null, ObserverFlags.None, null);
    }

    public void Dispose() => _watch?.Dispose();
}
