using Avalonia.Controls;
using Avalonia.Threading;
using Tmds.DBus.Protocol;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Tray;

namespace VExplore.Windows;

// A widget an app offers over D-Bus (org.vde.Widget1): the card it sends is drawn with the shell's controls, and the card is asked for
// again whenever the app says it changed. The app can be a Flatpak: the shell calls it, so it needs only to own its bus name.
internal sealed class AppWidgetView : ContentControl
{
    private static Task<DBusConnection?>? _bus;

    private WidgetAppClient? _client;

    public AppWidgetView(string service, string path)
    {
        Content = new TextBlock { Text = "Loading…", Opacity = 0.7 };
        _ = StartAsync(service, path);
    }

    // One session bus connection for every app widget.
    private static Task<DBusConnection?> Bus() => _bus ??= ConnectAsync();

    private static async Task<DBusConnection?> ConnectAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(DBusAddress.Session)) return null;
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            return connection;
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException) { return null; }
    }

    private async Task StartAsync(string service, string path)
    {
        if (await Bus() is not { } connection) { Show("There is no session bus."); return; }
        _client = new WidgetAppClient(connection, service, path);
        _client.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        try { await _client.WatchAsync(); } catch (DBusExceptionBase) { }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_client is null) return;
        var json = await _client.GetCardAsync();
        var card = json is null ? null : WidgetCard.Parse(json);
        if (card is null) { Show("This widget is not answering."); return; }
        Content = WidgetCardView.Build(card, action => _ = _client.ActivateAsync(action));
    }

    private void Show(string text) => Dispatcher.UIThread.Post(() => Content = new TextBlock { Text = text, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
}
