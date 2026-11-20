using System.Runtime.InteropServices;
using System.Net.Sockets;
using Vde.PamSam;

const string Usage = """
    pamsam: HKEY_LOCAL_MACHINE\SAM, made from the system's user accounts.
    usage: pamsam [--socket PATH | --socket-activation]
      --socket PATH         listen here (default /run/pamsam/sam.sock)
      --socket-activation   take the socket from systemd (LISTEN_FDS)
      -h, --help
    Runs as root. Serves the account database (/etc/passwd, /etc/group and the facts of /etc/shadow PAM works from)
    as a registry hive with the registry daemon's protocol; each client sees what its own uid may see.

    """;

if (args.Any(a => a is "-h" or "--help")) { Console.Out.Write(Usage); return 0; }
string? socketPath = null;
bool activated = false;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--socket" when i + 1 < args.Length: socketPath = args[++i]; break;
        case "--socket-activation": activated = true; break;
        default: Console.Error.Write(Usage); return 1;
    }

using var stop = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

using var service = new SamService();
service.Watch();
try
{
    if (activated)
    {
        if (Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString() || !int.TryParse(Environment.GetEnvironmentVariable("LISTEN_FDS"), out var n) || n < 1)
        { Console.Error.WriteLine("pamsam: --socket-activation was given but systemd passed no socket"); return 3; }
        var inherited = new Socket(new System.Net.Sockets.SafeSocketHandle((IntPtr)3, ownsHandle: true));
        if (inherited.AddressFamily != AddressFamily.Unix) { Console.Error.WriteLine("pamsam: the socket from systemd is not a Unix socket"); return 3; }
        service.Server.Listen(inherited);
    }
    else
        service.Server.Listen(socketPath ?? SamService.DefaultSocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
}
catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"pamsam: cannot listen: {e.Message}");
    return 3;
}
Console.Error.WriteLine("pamsam: serving HKLM\\SAM");
try { await service.Server.RunAsync(stop.Token); } catch (OperationCanceledException) { }
return 0;
