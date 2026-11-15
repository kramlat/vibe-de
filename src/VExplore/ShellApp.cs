using Vde.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Theme;
using Vde.Shell.ComDlg;
using Vibe.Registry;
using VExplore.Windows;

namespace VExplore;

// The running shell: owns the Avalonia application, applies the theme from the registry (and follows it) and
// opens the windows. Which window opens first comes from the command line.
public sealed class ShellApp : Application
{
    public static ExplorerHost? Host { get; set; }
    public static Func<Window>? FirstWindow { get; set; }

    // One per process: follows HKCU\Software\Vde\Input and notices Apple keyboards being plugged in or removed.
    public static ShortcutStyleService? Shortcuts { get; private set; }

    // How the shell asks for more rights (sudo, with the password dialog). Null until the app has started.
    public static ElevationOptions? Elevation { get; private set; }
    private IDisposable? _poll;
    private IDisposable? _binding;

    public override void OnFrameworkInitializationCompleted()
    {
        if (Host is { } host)
        {
            var engine = new ThemeEngine(ThemeCatalog.Discover());
            _binding = engine.BindTo(host.CurrentUser);
            Shortcuts = new ShortcutStyleService(host.CurrentUser);
            Elevation = new ElevationOptions(
                new SudoBackend(rootPassword: ElevationSettings.AsksForRootPassword(host.LocalMachine)),
                new ElevationPromptDialog(() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.LastOrDefault()),
                RegistryIdentity.Current);
            _poll = Shortcuts.Poll(TimeSpan.FromSeconds(3));
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && FirstWindow is { } make)
            desktop.MainWindow = make();
        base.OnFrameworkInitializationCompleted();
    }

    public static AppBuilder Build() => AppBuilder.Configure<ShellApp>().UsePlatformDetect();

    // ---- what the windows need from the rest of the shell ----

    public static Window NewFileManager(ExplorerHost host, string? folder)
    {
        var xdg = XdgDirectories.Default;
        var mime = new MimeDatabase(xdg);
        var apps = DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment());
        var associations = new FileAssociations(xdg, mime, apps, Desktops(), host.Classes);
        var types = new FileTypes(mime, host.Classes, Locale.FromEnvironment());
        var shell = new ShellExecute(associations, mime, types: types, elevation: Elevation);
        var start = folder is null ? new KnownFolders(xdg).Get(KnownFolder.Home) : AddressPath.IsShellPath(folder) ? folder : Path.GetFullPath(folder);
        return new FileManagerWindow(start, new KnownFolders(xdg), path =>
        {
            _ = OpenAsync(shell, path, associations, apps);   // no application: the chooser; an untrusted command: the password prompt
        }, types, shortcuts: Shortcuts, properties: paths => PropertyContext(host, paths), shellFolders: ShellFolders(host));
    }

    // What a Properties window needs: file types, rename, the tabs registered components add, and the groups the user is in.
    public static PropertySheetContext PropertyContext(ExplorerHost host, IReadOnlyList<string> paths)
    {
        var xdg = XdgDirectories.Default;
        var mime = new MimeDatabase(xdg);
        var types = new FileTypes(mime, host.Classes, Locale.FromEnvironment());
        var associations = new FileAssociations(xdg, mime, DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment()), Desktops(), host.Classes);
        return new PropertySheetContext
        {
            Target = new PropertyTarget(paths, paths.Count == 1 ? types.GetMimeType(paths[0]) : ""),
            Types = types,
            Actions = new FileActions(new Trash(xdg)),
            Handlers = host.PropertyHandlers,
            Loader = host.Loader,
            OpensWith = m => associations.GetDefault(m)?.Name,
            ChangeOpensWith = m => _ = CommonDialogs.ChooseApplicationAsync(ActiveWindow, associations, DesktopEntryDatabase.Load(xdg, Locale.FromEnvironment()), Desktops(), m),
            BuiltInProviders = [(PropertyPages.Clsid, new PropertyPages(Elevation))],   // the shell's own tabs: Permissions, Checksums, Customize
        };
    }

    // Shell folders: the shell's own (built in) plus whatever components registered, loaded on the UI thread through the host's loader.
    public static ShellFolderCatalog ShellFolders(ExplorerHost host)
    {
        var catalog = new ShellFolderCatalog(host.Classes, host.LocalMachine, host.CurrentUser, host.Loader);
        ShellFolderSetup.AddBuiltIns(catalog);
        ControlPanelShellFolder.Opener = item =>
        {
            if (item.Entry is { } entry) ExplorerCommand.Launch(host, entry);
            else if (item.Command is { } command) ExplorerCommand.Launch(host, command);
            else new ShellExecute(new FileAssociations(XdgDirectories.Default, new MimeDatabase(XdgDirectories.Default), DesktopEntryDatabase.Load(XdgDirectories.Default, Locale.FromEnvironment()), Desktops(), host.Classes), new MimeDatabase(XdgDirectories.Default)).Run("vexplore --settings");
        };
        return catalog;
    }

    public static Window? ActiveWindow => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.LastOrDefault();

    // Opens a path. When nothing is associated with its type, asks "How do you want to open this?" and opens it with the
    // choice (making it the default when the user ticks "always").
    public static async Task OpenAsync(ShellExecute shell, string path, FileAssociations? associations = null, DesktopEntryDatabase? apps = null)
    {
        try { await shell.OpenAsync(path); }
        catch (OperationCanceledException) { }   // the user said no
        catch (NoAssociationException e) when (associations is not null && apps is not null)
        {
            var app = await CommonDialogs.ChooseApplicationAsync(ActiveWindow, associations, apps, Desktops(), e.MimeType);
            if (app is null) return;
            try { shell.OpenWith(app, [path]); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { Console.Error.WriteLine("vexplore: " + ex.Message); }
        }
        catch (Exception e) when (e is NoAssociationException or FileNotFoundException or UntrustedCommandException or ElevationDeniedException or InvalidOperationException)
        {
            Console.Error.WriteLine("vexplore: " + e.Message);
        }
    }

    public static IReadOnlyList<string> Desktops()
        => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Prepend("VDE").ToList();
}
