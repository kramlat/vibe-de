using Tmds.DBus.Protocol;
using Vde.Shell.Notifications;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace VdeNotify;

// VDENotify: the notification daemon. It owns org.freedesktop.Notifications on the session bus, keeps the
// notifications in a NotificationService and follows the user's settings in the registry
// (HKCU\Software\Vde\Notifications, e.g. DoNotDisturb). Run by systemd as a user service, started on demand by
// the first application that sends a notification.
//
// It does not draw anything itself yet: every event is written to the log (journal). The shell's toast UI will
// listen to the same NotificationService.
public static class NotifyDaemon
{
    public const int Ok = 0, UsageError = 1, BusError = 2, LostBus = 3;

    public const string UsageText = """
        usage: VDENotify [options]

          --bus ADDRESS       D-Bus address to use (default: the session bus)
          --no-registry       do not read settings from the registry; use the defaults
          -h, --help

        Exit status: 0 ok, 1 usage, 2 cannot get org.freedesktop.Notifications, 3 lost the bus connection.

        """;

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
        => RunAsync(args, output, error, stop, Environment.GetEnvironmentVariable, registry: null);

    // registry: an already-open user hive (tests); null means open the real one (daemon if running, else file).
    internal static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken stop, Func<string, string?> env, IHive? registry)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            output.Write(UsageText);
            return Ok;
        }

        string? address = null;
        bool useRegistry = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bus" when i + 1 < args.Length: address = args[++i]; break;
                case "--no-registry": useRegistry = false; break;
                default:
                    error.WriteLine($"VDENotify: unknown or incomplete option '{args[i]}'");
                    return UsageError;
            }
        }
        address ??= DBusAddress.Session;
        if (address is null)
        {
            error.WriteLine("VDENotify: no session bus (DBUS_SESSION_BUS_ADDRESS is not set); use --bus");
            return UsageError;
        }

        var center = new NotificationService();
        center.Added += (_, n) => output.WriteLine($"notification {n.Id} from {n.AppName}: {n.Summary}{(string.IsNullOrEmpty(n.Body) ? "" : " - " + n.PlainBody)} [{n.Urgency}{(n.Silenced ? ", silenced" : "")}]");
        center.Updated += (_, n) => output.WriteLine($"notification {n.Id} updated: {n.Summary}");
        center.Closed += (_, e) => output.WriteLine($"notification {e.Id} closed: {e.Reason}");
        center.ActionInvoked += (_, e) => output.WriteLine($"notification {e.Id} action: {e.Key}");

        IDisposable? binding = null;
        RemoteHive? ownedRemote = null;
        RegistryStore? ownedStore = null;
        try
        {
            if (useRegistry)
            {
                IHive user;
                if (registry is not null)
                    user = registry;
                else
                {
                    ownedStore = FhiveClient.OpenStore();
                    user = ownedStore.CurrentUser;
                }
                binding = center.BindTo(user);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Notifications must work even when the registry does not: carry on with the defaults.
            error.WriteLine($"VDENotify: warning: cannot read settings from the registry ({e.Message}); using defaults");
        }

        NotificationsDBusServer server;
        try
        {
            server = await NotificationsDBusServer.StartAsync(center, address);
        }
        catch (Exception e) when (e is InvalidOperationException or DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            error.WriteLine($"VDENotify: cannot serve org.freedesktop.Notifications: {e.Message}");
            binding?.Dispose();
            ownedStore?.Dispose();
            return BusError;
        }

        error.WriteLine("VDENotify: serving org.freedesktop.Notifications");
        SystemdNotify.Ready(env);
        // Run until asked to stop, or until the bus goes away: then exit non-zero so systemd restarts us.
        var stopped = new TaskCompletionSource();
        using var registration = stop.Register(() => stopped.TrySetResult());
        var lost = server.DisconnectedAsync();
        bool busLost = await Task.WhenAny(stopped.Task, lost) == lost;
        if (busLost)
            error.WriteLine("VDENotify: lost the connection to the bus");

        SystemdNotify.Stopping(env);
        server.Dispose();   // releases the bus name: the next daemon (or activation) can take it
        binding?.Dispose();
        ownedStore?.Dispose();
        ownedRemote?.Dispose();
        error.WriteLine("VDENotify: stopped");
        return busLost ? LostBus : Ok;
    }
}
