using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Compositor;
using VExplore.Windows;

namespace VExplore;

public static class ExplorerCommand
{
    // The product name; the executable is vexplore.
    public const string ProductName = "Vibe Explorer";

    public const string UsageText = """
        Vibe Explorer: the VDE shell (taskbar, notification flyout, file manager).
        usage: vexplore [folder]
               vexplore --files [folder]    a file manager window (the default)
               vexplore --settings          the Settings (Control Panel) window
               vexplore --launcher          the app launcher
               vexplore --desktop           the desktop: wallpaper and the icons of ~/Desktop
               vexplore --chrome            draws window titlebars for vwm (needs vwm running; no window of its own)
               vexplore --taskbar           the taskbar, docked to the edge in HKCU\Software\Vde\Taskbar
               vexplore --list-components   show registered components and whether the shell would load them
               -h, --help

        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            output.Write(UsageText);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--list-components")
        {
            using var host = ExplorerHost.Open();
            return ListComponents(host, output);
        }

        var window = args.Length switch
        {
            0 => "files",
            _ when args[0] == "--settings" && args.Length == 1 => "settings",
            _ when args[0] == "--launcher" && args.Length == 1 => "launcher",
            _ when args[0] == "--desktop" && args.Length == 1 => "desktop",
            _ when args[0] == "--taskbar" && args.Length == 1 => "taskbar",
            _ when args[0] == "--chrome" && args.Length == 1 => "chrome",
            _ when args[0] == "--files" && args.Length <= 2 => "files",
            _ when !args[0].StartsWith('-') && args.Length == 1 => "files",
            _ => null,
        };
        if (window is null)
        {
            error.Write(UsageText);
            return 1;
        }
        var folder = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (window == "files" && folder is not null && !Directory.Exists(folder))
        {
            error.WriteLine($"vexplore: {folder}: not a folder");
            return 2;
        }

        using var host2 = ExplorerHost.Open();
        ShellApp.Host = host2;
        if (window == "chrome")
            return RunChrome(host2, error);
        ShellApp.FirstWindow = () => window switch
        {
            "settings" => NewSettings(host2),
            "launcher" => NewLauncher(host2),
            "desktop" => NewDesktop(host2),
            "taskbar" => NewTaskbar(host2),
            _ => ShellApp.NewFileManager(host2, folder),
        };
        return ShellApp.Build().StartWithClassicDesktopLifetime([]);
    }

    internal static int ListComponents(ExplorerHost host, TextWriter output)
    {
        foreach (var r in host.LoadableComponents)
            output.WriteLine($"load     {r.Component.Clsid} {r.Component.ClassName} ({r.Component.ThreadingModel})");
        foreach (var r in host.RefusedComponents)
            output.WriteLine($"refused  {r.Component.Clsid} {r.Component.ClassName}: {r.Trust.Reason}");
        return 0;
    }

    private static Window NewSettings(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var apps = DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment());
        var catalog = ControlPanelCatalog.Load(null, host.CurrentUser, apps, ShellApp.Desktops());
        var folders = new KnownFolders(xdg);
        var wallpapers = WallpaperCatalog.Find(xdg);
        return new ControlPanelWindow(catalog, host.CurrentUser, item =>
        {
            if (item.Entry is { } entry)
                Launch(host, entry);
            else if (item.Command is { } command)
                Launch(host, command);
        });
    }

    // The window list comes from vwm when it is running; elsewhere the taskbar only shows its pinned apps.
    private static IWindowSource Windows()
    {
        var client = new VwmClient();
        return client.Connect() ? client : new NoWindowSource();
    }

    // Titlebars for vwm: the same Avalonia theme as every other window, rendered offscreen and sent over its socket.
    internal static int RunChrome(ExplorerHost host, TextWriter error)
    {
        var client = new VwmClient();
        if (!client.Connect())
        {
            error.WriteLine("vexplore: vwm is not running (no control socket at " + VwmClient.DefaultSocketPath + ")");
            return 1;
        }
        Avalonia.AppBuilder.Configure<ChromeApp>().UseSkia()
            .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var engine = new Vde.Shell.Theme.ThemeEngine(Vde.Shell.Theme.ThemeCatalog.Discover());
        using var binding = engine.BindTo(host.CurrentUser);
        var apps = DesktopEntryDatabase.Load(XdgDirectories.Default, Locale.FromEnvironment());
        var service = new ChromeService(client, new ChromeRenderer(apps));
        engine.Changed += (_, _) => service.RedrawAll();
        void PushWindowSettings()
        {
            var value = host.CurrentUser.GetValue(BuiltInPages.WindowsKey, "TitlebarDoubleClick") is { Kind: Vibe.Registry.RegistryValueKind.String, Data: string s } ? s : "Maximize";
            client.SetTitlebarDoubleClick(value.ToLowerInvariant());
        }
        PushWindowSettings();
        host.CurrentUser.Changed += (_, c) => { if (c.IsUnder(BuiltInPages.WindowsKey)) PushWindowSettings(); };
        service.Run(CancellationToken.None);
        return 0;
    }

    private sealed class ChromeApp : Application { }

    private static Window NewTaskbar(ExplorerHost host)
    {
        var apps = DesktopEntryDatabase.Load(XdgDirectories.Default, Locale.FromEnvironment());
        var model = new TaskbarModel(host.CurrentUser, apps, Windows(), entry => Launch(host, entry));
        return new TaskbarWindow(model, () => Launch(host, "vexplore --launcher"));
    }

    private static Window NewDesktop(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var folders = new KnownFolders(xdg);
        var mime = new MimeDatabase(xdg);
        var apps = DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment());
        var associations = new FileAssociations(xdg, mime, apps, ShellApp.Desktops(), host.Classes);
        var types = new FileTypes(mime, host.Classes, Locale.FromEnvironment());
        var shell = new ShellExecute(associations, mime, types: types, elevation: ShellApp.Elevation);
        return new DesktopWindow(folders.Get(KnownFolder.Desktop), host.CurrentUser, path =>
        {
            try
            {
                if (Directory.Exists(path)) new ShellExecute(associations, mime, types: types).Run("vexplore --files " + ExecLineQuote(path));
                else _ = ShellApp.OpenAsync(shell, path, associations, apps);
            }
            catch (Exception e) when (e is FileNotFoundException or ArgumentException)
            {
                Console.Error.WriteLine("vexplore: " + e.Message);
            }
        }, types, openSettings: () => { try { shell.Run("vexplore --settings"); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } }, shortcuts: ShellApp.Shortcuts, properties: paths => ShellApp.PropertyContext(host, paths));
    }

    private static string ExecLineQuote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static Window NewLauncher(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var apps = DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment());
        var settings = ControlPanelCatalog.Load(null, host.CurrentUser, apps, ShellApp.Desktops());
        var model = new LauncherModel(host.CurrentUser, apps, ShellApp.Desktops(), settings)
        {
            FileRoots = [new KnownFolders(xdg).Get(KnownFolder.Documents), new KnownFolders(xdg).Get(KnownFolder.Desktop), new KnownFolders(xdg).Get(KnownFolder.Downloads)],
        };
        return new LauncherWindow(model, entry => Launch(host, entry), item =>
        {
            if (item.Entry is { } entry) Launch(host, entry);
        });
    }

    internal static void Launch(ExplorerHost host, DesktopEntry entry)
    {
        var xdg = XdgDirectories.Default;
        var mime = new MimeDatabase(xdg);
        var associations = new FileAssociations(xdg, mime, DesktopEntryDatabase.Load(xdg, []), ShellApp.Desktops(), host.Classes);
        try { new ShellExecute(associations, mime).Launch(entry); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException) { Console.Error.WriteLine("vexplore: " + e.Message); }
    }

    internal static void Launch(ExplorerHost host, string commandLine)
    {
        var xdg = XdgDirectories.Default;
        var mime = new MimeDatabase(xdg);
        var associations = new FileAssociations(xdg, mime, DesktopEntryDatabase.Load(xdg, []), ShellApp.Desktops(), host.Classes);
        try { new ShellExecute(associations, mime).Run(commandLine); }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException) { Console.Error.WriteLine("vexplore: " + e.Message); }
    }
}
