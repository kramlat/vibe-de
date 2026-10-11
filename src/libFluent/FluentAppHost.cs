using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;

namespace libFluent;

public static class FluentAppHost
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        BuildAvaloniaApp().SetupWithoutStarting();
        _initialized = true;
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<FluentDummyApp>()
                     .UsePlatformDetect()
                     .UseReactiveUI();
}

internal class FluentDummyApp : Application
{
}
