using Avalonia;

namespace libFluent;

public static class FluentAppHost
{
    // libFluent never owns the AppBuilder: the host application configures the platform
    // (UsePlatformDetect, UseWayland, ...) and libFluent only checks that one is running.
    public static void EnsureInitialized()
    {
        if (Application.Current is null)
            throw new InvalidOperationException("libFluent requires a running Avalonia Application.");
    }
}
