using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Vde.Shell.Controls.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Vde.Shell.Controls.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    // Real Skia rendering (not the no-op headless drawing) so screenshots show what users would see.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
