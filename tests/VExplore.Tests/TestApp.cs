using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using VExplore.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace VExplore.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
