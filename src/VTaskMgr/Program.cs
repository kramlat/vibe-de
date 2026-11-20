using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;
using VTaskMgr;

return AppBuilder.Configure<App>().UsePlatformDetect().UseVdeRender().UseWaylandWithFallback().StartWithClassicDesktopLifetime(args);

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        IHive hive;
        try { hive = Vibe.Registry.Remote.FhiveClient.OpenStore().CurrentUser; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { hive = new Hive("HKCU"); }
        var engine = new ThemeEngine(ThemeCatalog.Discover());
        engine.BindTo(hive);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.MainWindow = new TaskManagerWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
