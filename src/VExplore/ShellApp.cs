using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Theme;
using VExplore.Windows;

namespace VExplore;

// The running shell: owns the Avalonia application, applies the theme from the registry (and follows it) and
// opens the windows. Which window opens first comes from the command line.
public sealed class ShellApp : Application
{
    public static ExplorerHost? Host { get; set; }
    public static Func<Window>? FirstWindow { get; set; }
    private IDisposable? _binding;

    public override void OnFrameworkInitializationCompleted()
    {
        if (Host is { } host)
        {
            var engine = new ThemeEngine(ThemeCatalog.Discover());
            _binding = engine.BindTo(host.CurrentUser);
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
        var shell = new ShellExecute(associations, mime, types: types);
        var start = folder is null ? new KnownFolders(xdg).Get(KnownFolder.Home) : Path.GetFullPath(folder);
        return new FileManagerWindow(start, new KnownFolders(xdg), path =>
        {
            try { shell.Open(path); }
            catch (Exception e) when (e is NoAssociationException or FileNotFoundException or UntrustedCommandException or InvalidOperationException)
            {
                Console.Error.WriteLine("vexplore: " + e.Message);
            }
        }, types);
    }

    public static IReadOnlyList<string> Desktops()
        => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Prepend("VDE").ToList();
}
