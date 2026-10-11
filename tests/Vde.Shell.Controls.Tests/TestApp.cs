using Avalonia;
using Avalonia.Headless;
using Vde.Freedesktop;
using Vde.Shell.Controls.Tests;
using Vde.Shell.Theme;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Vde.Shell.Controls.Tests;

public class TestApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        TestTheme.Reset();
        base.OnFrameworkInitializationCompleted();
    }
}

// The same engine the desktop uses, pointed at an empty XDG tree so a developer's own themes never leak in.
public static class TestTheme
{
    private static ThemeEngine? _engine;

    public static ThemeEngine Engine => _engine!;

    public static XdgDirectories EmptyXdg(string? dataHome = null) => new(k => k switch
    {
        "HOME" => "/nonexistent-home",
        "XDG_DATA_HOME" => dataHome ?? "/nonexistent-data",
        "XDG_DATA_DIRS" => "/nonexistent-system",
        _ => null,
    });

    // Fluent, light/dark following the system: the starting point for every test.
    public static void Reset(ThemeCatalog? catalog = null)
    {
        _engine?.Uninstall();
        _engine = new ThemeEngine(catalog ?? ThemeCatalog.Discover(EmptyXdg()));
        _engine.Apply(new ThemeSelection("fluent"));
    }
}

public static class TestAppBuilder
{
    // Real Skia rendering (not the no-op headless drawing) so screenshots show what users would see.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
