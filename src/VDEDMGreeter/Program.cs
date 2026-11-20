using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Vde.DisplayManager;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;
using VdedmGreeter;

if (args is ["-h" or "--help"])
{
    Console.WriteLine("usage: vdedm-greeter [--demo]\n\nThe VDE login screen. Started by vdedm (inside vwm) with VDEDM_SOCK set;\n--demo shows it without a display manager (any user, password \"demo\").");
    return 0;
}
return AppBuilder.Configure<GreeterApp>().UsePlatformDetect().UseVdeRender().StartWithClassicDesktopLifetime(args);

public sealed class GreeterApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var demo = Environment.GetCommandLineArgs().Contains("--demo");
        IHive? hklm = null;
        try { if (File.Exists(RegistryStore.DefaultSystemHivePath)) hklm = Hive.OpenShared("HKLM", RegistryStore.DefaultSystemHivePath, watch: false, readOnly: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        var config = DmConfig.Load(hklm);

        // the theme, and the wallpaper, come from the system registry: nobody is logged in yet
        if (hklm is not null) new ThemeEngine(ThemeCatalog.Discover()).BindTo(hklm);
        var background = hklm is null ? null : WallpaperBrush.Create(DesktopSettingsStore.Load(hklm));

        IGreeterConnection connection;
        if (demo) connection = new DemoConnection();
        else
        {
            var socket = GreeterConnection.SocketFromEnvironment(Environment.GetEnvironmentVariable);
            if (socket is null) { Console.Error.WriteLine("vdedm-greeter: VDEDM_SOCK is not set (it is started by vdedm; try --demo)"); Environment.Exit(1); }
            try { connection = GreeterConnection.Connect(socket!); }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException) { Console.Error.WriteLine("vdedm-greeter: cannot reach vdedm: " + e.Message); Environment.Exit(1); return; }
        }

        var users = UserDirectory.Load();
        if (demo && users.Count == 0) users = [new LoginUser("demo", 1000, 1000, "/home/demo", "/bin/sh", "Demo User")];
        var sessions = SessionDirectory.Find(XdgDirectories.Default.DataDirs);
        if (demo && sessions.Count == 0) sessions = [new SessionEntry("vde", "Vibe Desktop Environment", "", ["start-vde"], SessionKind.Wayland, "VDE")];
        var model = new GreeterModel(connection, users, sessions, config.DefaultSession);
        var window = new GreeterWindow(model, background, power: Power);
        if (demo) { window.WindowState = WindowState.Normal; window.WindowDecorations = Avalonia.Controls.WindowDecorations.Full; window.Width = 1100; window.Height = 700; }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.MainWindow = window;
            model.SessionStarted += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => lifetime.Shutdown());   // the display manager takes over from here
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Power(string action)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("systemctl", action) { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

// Stands in for the display manager in --demo: password "demo" works, anything else does not.
internal sealed class DemoConnection : IGreeterConnection
{
    private bool _authenticated;
    public Task<GreeterResponse> SendAsync(GreeterRequest request, CancellationToken cancel = default)
    {
        GreeterResponse reply = request switch
        {
            CreateSession => new AuthMessage(AuthMessageType.Secret, "Password: "),
            PostAuthMessageResponse { Response: "demo" } => Authed(),
            PostAuthMessageResponse => new ErrorResponse(ErrorType.AuthError, "Authentication failure"),
            StartSession when _authenticated => new Success(),
            _ => new Success(),
        };
        return Task.FromResult(reply);
    }
    private Success Authed() { _authenticated = true; return new Success(); }
    public void Dispose() { }
}
