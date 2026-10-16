using System.Diagnostics;
using System.Runtime.InteropServices;
using Vde.DisplayManager;
using Vde.Freedesktop;
using Vibe.Registry;
using Vdedm;

const string Usage = """
    usage: vdedm [--check]

      (no option)   run the display manager (as root, from systemd: vdedm.service)
      --check       show what vdedm would do (configuration, accounts, sessions, PAM services) and start nothing

    Configured in HKLM\Software\Vde\DisplayManager (see docs/dm.md).

    """;

if (args is ["-h" or "--help"]) { Console.Write(Usage); return 0; }
if (args.Length > 0 && args is not ["--check"]) { Console.Error.Write(Usage); return 2; }

IHive? hklm = null;
try
{
    if (File.Exists(RegistryStore.DefaultSystemHivePath))
        hklm = Hive.OpenShared("HKLM", RegistryStore.DefaultSystemHivePath, watch: false, readOnly: true);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"vdedm: warning: cannot read the system registry ({e.Message}); using defaults");
}
var config = DmConfig.Load(hklm);
var system = new SystemDm();
var sessions = () => SessionDirectory.Find(XdgDirectories.Default.DataDirs, n => ProgramFinder.Find(n) is not null);

if (args is ["--check"])
{
    Console.WriteLine($"running as uid {Environment.UserName}{(Environment.UserName == "root" ? "" : "  (the real daemon needs root)")}");
    Console.WriteLine($"enabled: {config.Enabled}   terminal: tty{config.Vt}   greeter account: {config.GreeterUser} ({(system.FindUser(config.GreeterUser) is null ? "MISSING" : "ok")})");
    Console.WriteLine($"greeter: {string.Join(' ', config.GreeterCommand)}  ({(ProgramFinder.Find(config.GreeterCommand[0]) is null ? "NOT FOUND" : "found")})");
    Console.WriteLine($"launcher: {(ProgramFinder.Find("vterm-launch") ?? "NOT FOUND")}");
    foreach (var service in new[] { config.PamService, config.GreeterPamService }.Concat(config.AutoLoginUser is null ? [] : [config.AutologinPamService]))
        Console.WriteLine($"pam service {service}: {(File.Exists("/etc/pam.d/" + service) ? "ok" : "MISSING in /etc/pam.d")}");
    Console.WriteLine($"autologin: {config.AutoLoginUser ?? "off"}");
    Console.WriteLine("users:");
    foreach (var u in UserDirectory.Load()) Console.WriteLine($"  {u.Name,-16} {u.FullName}");
    Console.WriteLine("sessions:");
    foreach (var s in sessions()) Console.WriteLine($"  {s.Id,-16} {s.Name}  [{s.Kind}]  {string.Join(' ', s.Command)}{(s.Kind == SessionKind.X11 ? "  (X11: not startable yet)" : "")}");
    return 0;
}

if (!config.Enabled) { Console.WriteLine("vdedm: disabled in the registry (DisplayManager\\Enabled = 0)"); return 0; }
if (Environment.UserName != "root") { Console.Error.WriteLine("vdedm: the display manager must run as root (it starts desktops as other users)"); return 1; }
if (ProgramFinder.Find("vterm-launch") is null) { Console.Error.WriteLine("vdedm: vterm-launch is not installed"); return 1; }

using var stop = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

var daemon = new Daemon(config, new PamAuthenticator(config.PamService), new PamAuthenticator(config.GreeterPamService), new PamAuthenticator(config.AutologinPamService),
    system, sessions, Console.Out);
await daemon.RunAsync(stop.Token);
return 0;

namespace Vdedm
{
    // Finds our own programs where VDE installs them, then on PATH.
    public static class ProgramFinder
    {
        public static string? Find(string name)
        {
            if (name.Contains('/')) return File.Exists(name) ? name : null;
            var dirs = new List<string>();
            if (Environment.GetEnvironmentVariable("VDE_LIBEXEC") is { Length: > 0 } libexec) dirs.Add(libexec);
            dirs.Add("/usr/lib/vde");
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries));
            return dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        }
    }

    // The real machine behind IDmSystem.
    public sealed class SystemDm : IDmSystem
    {
        public string Launcher => ProgramFinder.Find("vterm-launch") ?? "/usr/lib/vde/vterm-launch";
        public void SwitchVt(int vt) => VirtualTerminals.Switch(vt);
        public void GiveTty(string tty, int uid, int gid) => VirtualTerminals.Give(tty, uid, gid);
        public LoginUser? FindUser(string name) => UserDirectory.Find(name);

        public IManagedProcess Start(LaunchPlan plan)
        {
            // the command may be a bare name (vwm): find it, as the user would not have our PATH
            var command = plan.Command.ToList();
            var launcher = command[0];
            var dash = command.IndexOf("--");
            if (dash >= 0 && dash + 1 < command.Count && !command[dash + 1].Contains('/') && ProgramFinder.Find(command[dash + 1]) is { } found)
                command[dash + 1] = found;
            return new Managed(UserProcesses.Start(plan with { Command = command }));
        }

        private sealed class Managed : IManagedProcess
        {
            private readonly Process _process;
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Managed(Process process)
            {
                _process = process;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => _exit.TrySetResult(process.ExitCode);
                if (process.HasExited) _exit.TrySetResult(process.ExitCode);
            }
            public Task<int> Exited => _exit.Task;
            public void Terminate() { try { if (!_process.HasExited) kill(_process.Id, 15); } catch (InvalidOperationException) { } }
            public void Kill() { try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
        }
    }
}
