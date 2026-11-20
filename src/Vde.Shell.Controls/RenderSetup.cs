using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.X11;

namespace Vde.Shell.Controls;

// The one render setup for the shell's programs: the GPU pipelines first, software as the last resort.
// On X11 the renderer is picked per the platform options (Vulkan needs libvulkan and a device; nuance:
// Avalonia.Wayland has no Vulkan backend in 12.1.6 — under Wayland the EGL/dmabuf pipeline is the GPU one).
// The chosen backend is written to the log at startup: a session that "feels heavy" usually turns out to
// be running software without anyone having noticed.
public static class RenderSetup
{
    // VDE_RENDER is the escape hatch: "software" stays on the CPU pipeline entirely, "gl" skips the Vulkan
    // attempt but keeps EGL (a broken driver layer must not lock the user out; no rebuild needed).
    public static AppBuilder UseVdeRender(this AppBuilder builder)
    {
        ShellIconProvider.Async = Environment.GetEnvironmentVariable("VDE_ICONS") != "sync";   // icons are made off the UI thread (and kept on disk), so no window waits for them
        var mode = Environment.GetEnvironmentVariable("VDE_RENDER")?.Trim().ToLowerInvariant();
        if (mode == "software")
            return builder;

        X11RenderingMode[] modes =
            mode == "gl"
                ? [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software]
                : [X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software];
        return builder.With(new X11PlatformOptions { RenderingMode = modes });
    }

    // One console line naming what is drawing: the platform implementation and its surfaces (the surface
    // types say GPU framebuffer or no surfaces = the CPU). Read it in the session log.
    public static void LogRenderer(TopLevel? window)
    {
        try
        {
            if (window?.PlatformImpl is not { } impl)
                throw new InvalidOperationException("no platform implementation");
            // Surfaces is protected in Avalonia 12; the runtime type names are all we need from it.
            var surfacesValue = impl.GetType()
                .GetProperty("Surfaces", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(impl) as System.Collections.IEnumerable;
            var surfaces = surfacesValue?.Cast<object>().Select(s => s.GetType().Name).ToList() ?? [];
            Console.Error.WriteLine("render: " + impl.GetType().Name + " [" + string.Join(", ", surfaces) + "]");
        }
        catch (Exception)
        {
            Console.Error.WriteLine("render: unknown");
        }
    }
}
