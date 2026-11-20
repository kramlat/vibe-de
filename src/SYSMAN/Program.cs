using System.Net.Sockets;
using System.Runtime.InteropServices;
using Vde.SysMan;

const string Usage = """
    sysman: HKEY_LOCAL_MACHINE\System, a Windows-shaped state of the machine.
    usage: sysman [--socket PATH | --socket-activation]
      --socket PATH         listen here (default /run/sysman/system.sock)
      --socket-activation   take the socket from systemd (LISTEN_FDS)
      -h, --help
    Runs as root. Plugins: control set, device classes, Enum (PCI, USB via udev), services (systemd units), network, hardware profile. Serves the result as a registry
    hive with the registry daemon's protocol; each client sees what its own uid may see.

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

using var service = new SystemService();
service.Watch();
try
{
    if (activated)
    {
        if (Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString() || !int.TryParse(Environment.GetEnvironmentVariable("LISTEN_FDS"), out var n) || n < 1)
        { Console.Error.WriteLine("sysman: --socket-activation was given but systemd passed no socket"); return 3; }
        var inherited = new Socket(new SafeSocketHandle((IntPtr)3, ownsHandle: true));
        if (inherited.AddressFamily != AddressFamily.Unix) { Console.Error.WriteLine("sysman: the socket from systemd is not a Unix socket"); return 3; }
        service.Server.Listen(inherited);
    }
    else
        service.Server.Listen(socketPath ?? SystemService.DefaultSocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
}
catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"sysman: cannot listen: {e.Message}");
    return 3;
}
Console.Error.WriteLine("sysman: serving HKLM\\System");
try { await service.Server.RunAsync(stop.Token); } catch (OperationCanceledException) { }
return 0;
