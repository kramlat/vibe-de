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
               vexplore --controlpanel      the Control Panel: the settings catalog as category links
               vexplore --launcher          the app launcher
               vexplore --desktop           the desktop: wallpaper and the icons of ~/Desktop
               vexplore --chrome            draws window titlebars for vwm (needs vwm running; no window of its own)
               vexplore --lock              the lock screen (a preview: not secure yet, any key dismisses it)
               vexplore --taskbar           the taskbar, docked to the edge in HKCU\Software\Vde\Taskbar
               vexplore --list-components   show registered components and whether the shell would load them
               -h, --help
        (the registry editor is vderegedit, the Settings app is vdesettings)

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
            _ when args[0] == "--controlpanel" && args.Length == 1 => "controlpanel",
            _ when args[0] == "--launcher" && args.Length == 1 => "launcher",
            _ when args[0] == "--desktop" && args.Length == 1 => "desktop",
            _ when args[0] == "--taskbar" && args.Length == 1 => "taskbar",
            _ when args[0] == "--lock" && args.Length == 1 => "lock",
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
        if (window == "files" && folder is not null && !AddressPath.IsShellPath(folder) && !Directory.Exists(folder))
        {
            error.WriteLine($"vexplore: {folder}: not a folder");
            return 2;
        }

        using var host2 = ExplorerHost.Open();
        if (window == "chrome")
            return RunChrome(host2, error);
        ShellApp.Host = host2;   // the app installs the registry's theme, shortcut style and elevation from it: without it no control has a template
        ShellApp.FirstWindow = () => window switch
        {
            "controlpanel" => NewControlPanel(host2),
            "launcher" => NewLauncher(host2),
            "desktop" => NewDesktop(host2),
            "taskbar" => NewTaskbar(host2),
            "lock" => new LockScreenWindow(host2.CurrentUser, host2.LocalMachine),
            _ => ShellApp.NewFileManager(host2, folder),
        };
        StartupTrace.Mark("program started");
        return ShellApp.Build().StartWithClassicDesktopLifetime([]);
    }

    // vdesettings: the Settings app as its own program (the start menu, the desktop menu and the .desktop file launch it).
    public static int RunSettings(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Any(a => a is "-h" or "--help")) { output.Write(SettingsUsageText); return 0; }
        // vdesettings [--page <id>]: opens on that page ("widgets", "taskbar"...)
        string? page = args.Length == 2 && args[0] == "--page" ? args[1] : null;
        if (args.Length != 0 && page is null) { error.Write(SettingsUsageText); return 2; }
        using var host = ExplorerHost.Open();
        return RunFirstWindow(host, () => NewSettings(host, page));
    }

    // vderegedit: the registry editor as its own program.
    public static int RunRegEdit(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Any(a => a is "-h" or "--help")) { output.Write(RegEditUsageText); return 0; }
        if (args.Length != 0) { error.Write(RegEditUsageText); return 2; }
        using var host = ExplorerHost.Open();
        return RunFirstWindow(host, () => NewRegEdit(host));
    }

    public const string SettingsUsageText = """
        Vibe Settings: display, appearance, desktop, taskbar, windows and more.
        usage: vdesettings [--page <id>]

        """;

    public const string RegEditUsageText = """
        Vibe Registry Editor: the keys and values of the user and the system hive.
        usage: vderegedit

        """;

    // The shared tail of the entry points: show the first window and run the shell lifetime.
    private static int RunFirstWindow(ExplorerHost host, Func<Window> first)
    {
        ShellApp.Host = host;
        ShellApp.FirstWindow = first;
        StartupTrace.Mark("program started");
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

    private static Window NewSettings(ExplorerHost host, string? page = null)
    {
        var xdg = XdgDirectories.Default;
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var catalog = ControlPanelCatalog.Load(null, host.CurrentUser, apps, ShellApp.Desktops());
        var folders = new KnownFolders(xdg);
        var wallpapers = WallpaperCatalog.Find(xdg);
        var device = DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine);
        var settings = new ControlPanelWindow(catalog, host.CurrentUser, item =>
        {
            if (item.Entry is { } entry)
                Launch(host, entry);
            else if (item.Command is { } command)
                Launch(host, command);
        }, customPages: id => CustomPage(host, wallpapers, id), user: new UserProfile(), deviceSettings: device, wallpapers: wallpapers);
        WindowMica.Attach(settings, device);
        if (page is not null && catalog.Items.FirstOrDefault(i => string.Equals(i.PageId, page, StringComparison.OrdinalIgnoreCase)) is { } target)
            settings.Opened += (_, _) => settings.Open(target);
        return settings;
    }

    // Apps that can show a status on the lock screen: the installed weather, calendar, clock and mail programs.
    private static IReadOnlyList<SettingChoice> StatusApps() => ShellData.Apps(Locale.FromEnvironment()).Entries
        .Where(e => !e.NoDisplay && !e.Hidden && new[] { "weather", "calendar", "clock", "mail" }.Any(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => new SettingChoice(e.Id, e.Name)).ToList();

    // The pages of ours that are more than a list of settings.
    private static Avalonia.Controls.Control? CustomPage(ExplorerHost host, IReadOnlyList<WallpaperInfo> wallpapers, string id) => id switch
    {
        DesktopPageView.PageId => new DesktopPageView(host.CurrentUser, wallpapers, BrowseForWallpaperAsync, host.LocalMachine),
        DisplayPageView.PageId => new DisplayPageView(host.CurrentUser),
        LockScreenPageView.PageId => new LockScreenPageView(host.CurrentUser, wallpapers, StatusApps(), BrowseForWallpaperAsync, host.LocalMachine),
        ThemesPageView.PageId => new ThemesPageView(host.CurrentUser, Vde.Shell.Theme.ThemeCatalog.Discover(), host.LocalMachine),
        ColorsPageView.PageId => new ColorsPageView(host.CurrentUser, host.LocalMachine),
        FontsPageView.PageId => new FontsPageView(host.CurrentUser, InstallFontsAsync),
        _ => null,
    };

    // Fonts dropped on the Fonts page: into this user's folder, or for everyone through the password prompt (sudo).
    private static async Task<string> InstallFontsAsync(IReadOnlyList<string> paths, bool local)
    {
        var fonts = paths.Where(FontInstaller.IsFont).ToList();
        if (fonts.Count == 0)
            return FontsPageView.FontInstallText(new FontInstallResult([], paths));
        if (local)
        {
            var dir = FontInstaller.LocalDirectory();
            var result = FontInstaller.InstallInto(paths, dir);
            FontInstaller.RefreshCache(dir);
            return FontsPageView.FontInstallText(result);
        }
        if (ShellApp.Elevation is not { } elevation)
            return "Installing for all users needs elevation, which is not set up here.";
        await ElevationRunner.RunAsync(elevation, new ElevationRequest("Install fonts for all users", "install", fonts[0], FontInstaller.SystemCommand(fonts),
            "installing a font for every user writes to " + FontInstaller.SystemDirectory));
        return FontsPageView.FontInstallText(new FontInstallResult(fonts, paths.Except(fonts).ToList())) + " For all users.";
    }

    // The classic Control Panel: the catalog of items shown the Windows 7 way, category links first.
    private static Window NewControlPanel(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var catalog = ControlPanelCatalog.Load(null, host.CurrentUser, apps, ShellApp.Desktops());
        var folders = new KnownFolders(xdg);
        var wallpapers = WallpaperCatalog.Find(xdg);
        var window = new ClassicControlPanelWindow(catalog, host.CurrentUser, item =>
        {
            if (item.Entry is { } entry)
                Launch(host, entry);
            else if (item.Command is { } command)
                Launch(host, command);
        }, customPages: id => CustomPage(host, wallpapers, id));
        WindowMica.Attach(window, DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine));
        return window;
    }

    // The window list comes from vwm when it is running; elsewhere the taskbar only shows its pinned apps.
    // The Browse… button of the Desktop page: the system file picker, images only.
    private static async Task<string?> BrowseForWallpaperAsync(Avalonia.Controls.Window? owner)
    {
        if (owner?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a picture",
            AllowMultiple = false,
            FileTypeFilter = [Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll],
        });
        return picked.Count == 1 && picked[0].Path is { IsFile: true } uri ? uri.LocalPath : null;
    }

    // The registry editor edits the same two hives everything else goes through: the user's, writable, and the
    // system's, readable (writes to it want the daemon as root, which the hive's permissions decide).
    private static Window NewRegEdit(ExplorerHost host)
    {
        var window = new global::VExplore.Windows.RegEditWindow(host.CurrentUser, host.LocalMachine);
        WindowMica.Attach(window, DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine));
        return window;
    }

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
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var renderer = new ChromeRenderer(apps, DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine));
        var service = new ChromeService(client, renderer);
        void PushBorder() { var (focused, other) = renderer.BorderColors(); client.SetBorderColors(focused, other); }
        engine.Changed += (_, _) => { service.RedrawAll(); PushBorder(); };
        PushBorder();
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
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var model = new TaskbarModel(host.CurrentUser, apps, Windows(), entry => Launch(host, entry));
        StartMenuController? startMenu = null;
        var window = new TaskbarWindow(model, () => { if (startMenu is not null) startMenu.Toggle(); else Launch(host, "vexplore --launcher"); },
            settings: DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine));
        // Tell vwm this window is the taskbar: under Wayland there are no X11 struts, the role does it.
        var role = ClaimRole(TaskbarRole(model));
        window.Closed += (_, _) => role.Dispose();
        // Start is a popup of this process: the button, the Start key and Super+Space toggle it (see StartMenuController).
        startMenu = new StartMenuController(() => NewLauncher(host), window, model, role);
        window.View.SearchInvoked += (_, _) => startMenu.Toggle();   // Start opens ready to search
        window.View.TaskViewInvoked += (_, _) => role.TaskView();
        var widgets = new WidgetsController(() => NewWidgetsPanel(host), window, model, role);
        window.View.WidgetsInvoked += (_, _) => widgets.Toggle();
        window.View.ShowDesktopInvoked += (_, _) => model.ToggleShowDesktop();
        window.KeepVisible = () => startMenu.IsOpen || widgets.IsOpen;   // an auto-hiding bar stays while Start is open
        // the edge or auto-hide changed: vwm needs to hear the new role (an auto-hiding bar reserves no space)
        void AnnounceEdge() => role.AnnounceRole((uint)Environment.ProcessId, TaskbarRole(model));
        model.Changed += (_, _) => AnnounceEdge();
        window.View.TouchKeyboardInvoked += (_, _) => OnScreenKeyboard.Toggle();
        window.FollowAppearance(host.CurrentUser);
        // The tray: the shell becomes the session's StatusNotifierWatcher host and fills the slot.
        window.Opened += async (_, _) =>
        {
            var tray = new TrayAreaController(window.View, () => window.Dock(), host.CurrentUser);
            window.Closed += (_, _) => tray.Dispose();
            try { await tray.StartAsync(); }
            catch { /* a session without a bus just runs trayless */ }
        };
        return window;
    }

    // VDE's widgets panel: our cards plus the community's (components and apps that offer a widget), each only when the user allowed it.
    private static WidgetsPanelView NewWidgetsPanel(ExplorerHost host)
    {
        var shell = new ShellExecute(new FileAssociations(XdgDirectories.Default, ShellData.Mime(), ShellData.Apps([]), ShellApp.Desktops(), host.Classes), ShellData.Mime());
        return new WidgetsPanelView(host.CurrentUser,
            url => { try { shell.Open(url); } catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or NoAssociationException) { } },
            file => { try { shell.Open(file); } catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or NoAssociationException) { } },
            () => { try { shell.Run("vdesettings --page widgets"); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } },
            community: () => CommunityWidgets(host));
    }

    // The widgets the community made that the user allowed: components (trusted registrations) and apps with a D-Bus widget. Noted in the
    // registry as it goes so Settings can list them to be allowed.
    private static IReadOnlyList<CommunityWidget> CommunityWidgets(ExplorerHost host)
    {
        var providers = new WidgetProviders(host.Classes, host.CurrentUser);
        var apps = ShellData.Apps(Locale.FromEnvironment()).Entries
            .Where(e => e.WidgetBusName is { } bus && Vde.Shell.Tray.WidgetAppClient.IsValid(bus, e.WidgetPath)).ToList();
        providers.Remember(apps.Select(a => (a.Id, a.Name)));
        var list = new List<CommunityWidget>();
        foreach (var (id, widget) in providers.Load(host.Loader))
            list.Add(new CommunityWidget(WidgetProviders.KeyOf(id), widget.Title, widget.IconNames, () => (Avalonia.Controls.Control)widget.CreateContent()));
        foreach (var app in apps.Where(a => providers.IsAllowed(a.Id)))
            list.Add(new CommunityWidget(WidgetProviders.KeyOf(app.Id), app.Name, app.Icon is { Length: > 0 } icon ? [icon] : ["applications-utilities"], () => new AppWidgetView(app.WidgetBusName!, app.WidgetPath)));
        return list;
    }

    // What vwm is told the taskbar is: its edge, and "-auto" when it hides itself (then it reserves no space).
    private static string TaskbarRole(TaskbarModel model) => model.Edge switch
    {
        TaskbarEdge.Top => "top",
        TaskbarEdge.Left => "left",
        TaskbarEdge.Right => "right",
        _ => "bottom",
    } + (model.AutoHide ? "-auto" : "");

    private static Window NewDesktop(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var folders = new KnownFolders(xdg);
        var mime = ShellData.Mime();
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var associations = new FileAssociations(xdg, mime, apps, ShellApp.Desktops(), host.Classes);
        var types = new FileTypes(mime, host.Classes, Locale.FromEnvironment());
        var shell = new ShellExecute(associations, mime, types: types, elevation: ShellApp.Elevation);
        // Tell vwm this window is the desktop: it fills the screen and stays under everything.
        var role = ClaimRole("desktop");
        var window = new DesktopWindow(folders.Get(KnownFolder.Desktop), host.CurrentUser, path =>
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
        }, types, openSettings: () => { try { shell.Run("vdesettings"); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } }, shortcuts: ShellApp.Shortcuts, properties: paths => ShellApp.PropertyContext(host, paths), systemDefaults: host.LocalMachine, shellFolders: ShellApp.ShellFolders(host),
            openShellFolder: path => { try { shell.Run("vexplore --files " + ExecLineQuote(path)); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } },
            openPage: page => { try { shell.Run("vdesettings --page " + page); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } },
            openTerminal: dir => { try { shell.Run("vterminal --working-directory " + ExecLineQuote(dir)); } catch (Exception e) when (e is FileNotFoundException or ArgumentException) { } });
        window.Closed += (_, _) => role.Dispose();
        return window;
    }

    // Opens a connection to vwm just to claim a role (desktop / an edge) for this process's windows: under
    // Wayland there are no EWMH struts or window types for the desktop and the taskbar, so the shell says
    // what its windows are and vwm arranges them. Kept alive until the window closes by the caller.
    private static VwmClient ClaimRole(string role)
    {
        var claim = new VwmClient();
        if (claim.Connect())
            claim.AnnounceRole((uint)Environment.ProcessId, role);
        return claim;
    }

    private static string ExecLineQuote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static LauncherWindow NewLauncher(ExplorerHost host)
    {
        var xdg = XdgDirectories.Default;
        var apps = ShellData.Apps(Locale.FromEnvironment());
        var settings = ControlPanelCatalog.Load(null, host.CurrentUser, apps, ShellApp.Desktops());
        var model = new LauncherModel(host.CurrentUser, apps, ShellApp.Desktops(), settings)
        {
            FileRoots = [new KnownFolders(xdg).Get(KnownFolder.Documents), new KnownFolders(xdg).Get(KnownFolder.Desktop), new KnownFolders(xdg).Get(KnownFolder.Downloads)],
        };
        var window = new LauncherWindow(model, entry => Launch(host, entry), item =>
        {
            if (item.Entry is { } entry) Launch(host, entry);
        }, action => Footer(host, action, new KnownFolders(xdg).Get(KnownFolder.Home)),
            settings: DesktopSettingsStore.Load(host.CurrentUser, host.LocalMachine));
        return window;
    }

    // The start menu footer's buttons. Power actions go through logind (systemctl), which asks polkit as usual;
    // signing out ends the VDE session.
    private static void Footer(ExplorerHost host, LauncherFooterAction action, string home)
    {
        var command = action switch
        {
            LauncherFooterAction.Files => "vexplore --files " + ExecLineQuote(home),
            LauncherFooterAction.Settings => "vdesettings",
            LauncherFooterAction.Documents or LauncherFooterAction.Downloads or LauncherFooterAction.Music or LauncherFooterAction.Pictures
                or LauncherFooterAction.Videos or LauncherFooterAction.Network or LauncherFooterAction.Home
                => StartFolders.Location(action.ToString()) is { } place ? "vexplore --files " + ExecLineQuote(place) : null,
            LauncherFooterAction.SignOut => "vde-session --logout",
            LauncherFooterAction.Sleep => "systemctl suspend",
            LauncherFooterAction.Restart => "systemctl reboot",
            LauncherFooterAction.ShutDown => "systemctl poweroff",
            _ => null,
        };
        if (command is not null) Launch(host, command);
    }

    internal static void Launch(ExplorerHost host, DesktopEntry entry)
    {
        var xdg = XdgDirectories.Default;
        var mime = ShellData.Mime();
        var associations = new FileAssociations(xdg, mime, ShellData.Apps([]), ShellApp.Desktops(), host.Classes);
        try { new ShellExecute(associations, mime).Launch(entry); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException) { Console.Error.WriteLine("vexplore: " + e.Message); }
    }

    internal static void Launch(ExplorerHost host, string commandLine)
    {
        var xdg = XdgDirectories.Default;
        var mime = ShellData.Mime();
        var associations = new FileAssociations(xdg, mime, ShellData.Apps([]), ShellApp.Desktops(), host.Classes);
        try { new ShellExecute(associations, mime).Run(commandLine); }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException) { Console.Error.WriteLine("vexplore: " + e.Message); }
    }
}
