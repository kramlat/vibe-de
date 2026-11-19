using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Vde.Freedesktop;
using Vde.Shell.Theme;
using Vde.Terminal;
using Vibe.Registry;
using Vibe.Registry.Remote;
using VTerminal;

var options = Options.Parse(args);
if (options.Error is { } problem)
{
    if (problem == "help") { Console.Write(Options.Usage); return 0; }
    Console.Error.WriteLine("vterminal: " + problem);
    Console.Error.Write(Options.Usage);
    return 2;
}

if (Pty.FindLauncher() is null)
{
    Console.Error.WriteLine("vterminal: vterm-launch is not installed (build it with native/vterm/build.sh)");
    return 1;
}

return AppBuilder.Configure<App>().UsePlatformDetect().UseWaylandWithFallback().StartWithClassicDesktopLifetime([]);

public sealed class App : Application
{
    private readonly List<IDisposable> _signals = new();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var options = Options.Parse(args);
        IHive hive;
        try { hive = Vibe.Registry.Remote.FhiveClient.OpenStore().CurrentUser; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { hive = new Hive("HKCU"); }
        var engine = new ThemeEngine(ThemeCatalog.Discover());
        engine.BindTo(hive);
        var profiles = ShellProfiles.Detect(Environment.GetEnvironmentVariable, n => ShellProfiles.FindOnPath(n, Environment.GetEnvironmentVariable));
        var home = XdgDirectories.Default.Home;
        var start = options.WorkingDirectory is { } d && Directory.Exists(d) ? d : home;
        var window = new TerminalWindow(hive, profiles, start, options.Hold);
        if (options.Title is { } t) window.Title = t;
        if (options.Command.Count > 0)
            window.NewTab(new ShellProfile(Options.Name(options.Command), options.Command), start, options.Command, new Dictionary<string, string> { ["VDE_TERMINAL"] = "vterminal" });
        else
        {
            var profile = options.Profile is { } wanted ? profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)) ?? window.DefaultProfile() : window.DefaultProfile();
            window.NewTab(profile, start);
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.MainWindow = window;
            // being told to end (logout, kill) closes the windows properly, which also ends the programs running in them
            foreach (var signal in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGHUP, System.Runtime.InteropServices.PosixSignal.SIGINT })
                _signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(signal, c => { c.Cancel = true; Avalonia.Threading.Dispatcher.UIThread.Post(() => lifetime.Shutdown()); }));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
