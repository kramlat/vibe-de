using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using VdedmGreeter.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace VdedmGreeter.Tests;

public class TestApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Styles.Add(new FluentTheme());
        base.OnFrameworkInitializationCompleted();
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
