using System.Net.Sockets;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Fhived;

// fhived: the registry daemon. It owns one hive file and serves it on a Unix socket, applying the hive's
// permissions to the real identity of each client. The system instance owns /etc/hive/System.fhive and is
// reachable by everyone; the user instance owns the user's hive and serves only that user.
public static class FhivedCommand
{
    public const int Ok = 0, UsageError = 1, HiveError = 2, ListenError = 3;

    public const string UsageText = """
        usage: fhived (--system | --user) [options]

          --system             serve /etc/hive/System.fhive on /run/fhive/system.sock (socket mode 666)
          --user               serve ~/.config/hive/User.fhive on $XDG_RUNTIME_DIR/fhive/user.sock (socket mode 600,
                               only this user and root may connect)

        options:
          --socket PATH        socket to listen on (default depends on --system or --user)
          --hive PATH          hive file to serve
          --read-only          never write the hive (the default for --system unless running as root)
          --socket-activation  take the listening socket from systemd (LISTEN_FDS) instead of creating one
          --idle-exit SECONDS  exit after this long without any client (restarted by socket activation)
          -h, --help

        The system hive must already exist (mkhive creates it); the user hive is created if it is missing.
        Exit status: 0 ok, 1 usage, 2 hive problem, 3 cannot listen.

        """;

    private sealed class Options
    {
        public bool System, User, ReadOnly, SocketActivation;
        public string? Socket, Hive;
        public int? IdleExitSeconds;
    }

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
        => RunAsync(args, output, error, stop, Environment.GetEnvironmentVariable);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop, Func<string, string?> env)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            output.Write(UsageText);
            return Ok;
        }
        var options = Parse(args, error);
        if (options is null)
            return UsageError;

        var identity = RegistryIdentity.Current;
        var runtime = env("XDG_RUNTIME_DIR") is { Length: > 0 } r && Path.IsPathRooted(r) ? r : null;
        var config = env("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c)
            ? c
            : Path.Combine(env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        var hivePath = options.Hive ?? (options.System ? "/etc/hive/System.fhive" : Path.Combine(config, "hive", "User.fhive"));
        var socketPath = options.Socket ?? (options.System
            ? "/run/fhive/system.sock"
            : runtime is null ? null : Path.Combine(runtime, "fhive", "user.sock"));
        bool readOnly = options.ReadOnly || (options.System && !identity.IsRoot);

        if (socketPath is null && !options.SocketActivation)
        {
            error.WriteLine("fhived: no XDG_RUNTIME_DIR to put the socket in; use --socket");
            return UsageError;
        }

        Hive hive;
        try
        {
            if (options.System && !readOnly && !File.Exists(hivePath))
            {
                error.WriteLine($"fhived: the system hive {hivePath} does not exist; create it with mkhive first");
                return HiveError;
            }
            hive = Hive.OpenShared(options.System ? "HKLM" : "HKCU", hivePath, watch: true, readOnly: readOnly,
                fileMode: options.System ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"fhived: cannot open {hivePath}: {e.Message}");
            return HiveError;
        }

        // The user instance only serves its own user (and root, who could read the file anyway).
        Func<RegistryIdentity, bool>? accept = options.System ? null : peer => peer.IsRoot || peer.Uid == identity.Uid;

        using (hive)
        using (var server = new HiveServer(hive, accept: accept))
        {
            try
            {
                if (options.SocketActivation)
                    server.Listen(InheritedSocket(env));
                else
                    server.Listen(socketPath!, options.System
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite   // 666
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite);                                                                                                 // 600
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                error.WriteLine($"fhived: cannot listen: {e.Message}");
                return ListenError;
            }

            error.WriteLine($"fhived: serving {hivePath}{(readOnly ? " (read-only)" : "")} on {(options.SocketActivation ? "the systemd socket" : socketPath)}");
            SystemdNotify.Ready(env);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task? idle = options.IdleExitSeconds is { } seconds ? WatchIdleAsync(server, TimeSpan.FromSeconds(seconds), cancel) : null;
            await server.RunAsync(cancel.Token);
            SystemdNotify.Stopping(env);
            if (idle is not null)
                await idle;
        }
        error.WriteLine("fhived: stopped");
        return Ok;
    }

    // systemd passes the already-listening socket as file descriptor 3 (SD_LISTEN_FDS_START).
    private static Socket InheritedSocket(Func<string, string?> env)
    {
        if (env("LISTEN_PID") != Environment.ProcessId.ToString() || !int.TryParse(env("LISTEN_FDS"), out var count) || count < 1)
            throw new InvalidOperationException("--socket-activation was given but systemd passed no socket (LISTEN_FDS/LISTEN_PID)");
        var socket = new Socket(new SafeSocketHandle((IntPtr)3, ownsHandle: true));
        if (socket.AddressFamily != AddressFamily.Unix)   // the registry is local-only: refuse anything that is not a Unix socket
        {
            socket.Dispose();
            throw new InvalidOperationException("the socket passed by systemd is not a Unix-domain socket");
        }
        return socket;
    }

    private static async Task WatchIdleAsync(HiveServer server, TimeSpan limit, CancellationTokenSource cancel)
    {
        var idleSince = DateTime.UtcNow;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancel.Token);
                if (server.ConnectionCount > 0)
                    idleSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - idleSince >= limit)
                {
                    cancel.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Options? Parse(string[] args, TextWriter error)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string? Value()
            {
                if (i + 1 < args.Length)
                    return args[++i];
                error.WriteLine($"fhived: {args[i]} needs a value");
                return null;
            }

            switch (args[i])
            {
                case "--system": options.System = true; break;
                case "--user": options.User = true; break;
                case "--read-only": options.ReadOnly = true; break;
                case "--socket-activation": options.SocketActivation = true; break;
                case "--socket": if ((options.Socket = Value()) is null) return null; break;
                case "--hive": if ((options.Hive = Value()) is null) return null; break;
                case "--idle-exit":
                    if (Value() is not { } text || !int.TryParse(text, out var seconds) || seconds < 1)
                    {
                        error.WriteLine("fhived: --idle-exit needs a number of seconds");
                        return null;
                    }
                    options.IdleExitSeconds = seconds;
                    break;
                default:
                    error.WriteLine($"fhived: unknown option '{args[i]}'");
                    return null;
            }
        }

        if (options.System == options.User)
        {
            error.WriteLine("fhived: give exactly one of --system or --user");
            return null;
        }
        return options;
    }
}
