using Avalonia;
using Avalonia.Headless;
using Vde.Shell.Theme.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Vde.Shell.Theme.Tests;

// An application with no theme of its own: the engine under test installs one.
public class TestApp : Application
{
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
