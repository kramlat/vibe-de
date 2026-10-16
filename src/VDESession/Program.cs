using System.Runtime.InteropServices;
using Vde.Freedesktop;
using Vibe.Registry;
using Vibe.Registry.Remote;
using VdeSession;

const string Usage = """
    usage: vde-session [--logout | --status | --dry-run]

      (no option)   run the VDE session: start the compositor (vwm), the shell and the autostart programs
      --logout      end the running session
      --status      show the programs of the running session
      --dry-run     print what would be started, and start nothing

    Normally started by start-vde, which the login manager runs from vde.desktop.
    The programs are listed in HKCU\Software\Vde\Session\Components (Enabled, Command per program).

    """;

Func<string, string?> env = Environment.GetEnvironmentVariable;
var controlPath = SessionControl.DefaultPath(env);

if (args is ["-h" or "--help"]) { Console.Write(Usage); return 0; }
if (args is ["--logout"] or ["--status"])
{
    try
    {
        Console.Write(SessionControl.Ask(controlPath, args[0][2..]));
        return 0;
    }
    catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
    {
        Console.Error.WriteLine("vde-session: no VDE session is running");
        return 1;
    }
}
if (args.Length > 0 && args is not ["--dry-run"]) { Console.Error.Write(Usage); return 2; }

IHive? user = null;
RegistryStore? store = null;
try { store = FhiveClient.OpenStore(); user = store.CurrentUser; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Console.Error.WriteLine($"vde-session: warning: cannot read the registry ({e.Message}); using defaults"); }

using var storeLifetime = store;
var xdg = XdgDirectories.Default;
var components = SessionComponents.Load(user);
var autostart = Autostart.Find(xdg, ["VDE"]);

if (args is ["--dry-run"])
{
    foreach (var c in components) Console.WriteLine($"{(c.Enabled ? "start" : "off  "),-6}{c.Name,-14}{string.Join(' ', c.Command)}");
    foreach (var (name, command) in autostart) Console.WriteLine($"autostart    {name,-14}{string.Join(' ', command)}");
    return 0;
}

if (env("XDG_RUNTIME_DIR") is not { Length: > 0 })
{
    Console.Error.WriteLine("vde-session: XDG_RUNTIME_DIR is not set; start the session through start-vde (or a login manager)");
    return 2;
}
if (SessionControl.IsAlive(controlPath))
{
    Console.Error.WriteLine("vde-session: a VDE session is already running (vde-session --logout ends it)");
    return 2;
}

var envFile = Path.Combine(Path.GetDirectoryName(controlPath)!, "vwm.env");
var session = new SessionManager(new SystemProcessHost(), env, components, autostart, envFile, Console.Out)
{
    Export = ExportToServices,
};
using var control = new SessionControl(controlPath, session);

using var stop = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; stop.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

return await session.RunAsync(stop.Token);

// systemd user services and D-Bus-activated programs start outside our process tree: tell their managers where the display is.
static void ExportToServices(IReadOnlyDictionary<string, string> vars)
{
    var names = SessionEnvironment.Exported.Where(vars.ContainsKey).ToList();
    if (names.Count == 0) return;
    foreach (var (tool, args) in new[] { ("dbus-update-activation-environment", new[] { "--systemd" }), ("systemctl", new[] { "--user", "import-environment" }) })
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in args.Concat(names)) info.ArgumentList.Add(a);
            foreach (var n in names) info.Environment[n] = vars[n];
            System.Diagnostics.Process.Start(info)?.WaitForExit(3000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }   // not installed: nothing to tell
}
