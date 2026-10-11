namespace Vibe.Registry.Remote;

// The one call applications make to get at the registry: through the daemons if they are running (where
// permissions are enforced against the kernel's view of this process), otherwise by reading the hive files
// directly (the file-sharing mode, where permissions are checked by this library only). Nothing breaks on a
// machine without fhived; it is just not the strict mode.
public static class FhiveClient
{
    public const string SystemSocket = "/run/fhive/system.sock";

    // $XDG_RUNTIME_DIR/fhive/user.sock, or null when there is no runtime directory.
    public static string? UserSocket(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return env("XDG_RUNTIME_DIR") is { Length: > 0 } dir && Path.IsPathRooted(dir) ? Path.Combine(dir, "fhive", "user.sock") : null;
    }

    // HKLM and HKCU, each from its daemon if reachable, else from its file. Paths and sockets default to the
    // standard places; tests and unusual installs can pass their own.
    public static RegistryStore OpenStore(
        string? userHivePath = null, string? systemHivePath = null, string? userSocket = null, string? systemSocket = null, bool watch = true)
    {
        // A file is only opened for a hive with no daemon: opening it anyway would create it and watch it for nothing.
        IHive system = TryConnect(systemSocket ?? SystemSocket)
            ?? Hive.OpenShared("HKLM", systemHivePath ?? RegistryStore.DefaultSystemHivePath, watch, readOnly: true);
        IHive user = TryConnect(userSocket ?? UserSocket())
            ?? Hive.OpenShared("HKCU", userHivePath ?? RegistryStore.DefaultUserHivePath, watch);
        return new RegistryStore(system, user);
    }

    // True when the hive is served by a daemon, false when this process has the file itself.
    public static bool IsServedByDaemon(IHive hive) => hive is RemoteHive;

    private static IHive? TryConnect(string? socket)
    {
        if (socket is null || !File.Exists(socket))
            return null;
        try
        {
            return RemoteHive.Connect(socket);
        }
        catch (IOException)
        {
            return null;   // a stale socket file, or a daemon that is just going down
        }
    }
}
