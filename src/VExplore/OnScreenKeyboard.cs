using System.Diagnostics;

namespace VExplore;

// The taskbar's keyboard icon: shows or hides vde-osk (native/vosk), starting it when it is not running. A running
// vde-osk toggles on SIGUSR1; one that is not running is started already shown.
internal static class OnScreenKeyboard
{
    public static void Toggle()
    {
        try
        {
            using var signal = Process.Start(new ProcessStartInfo("pkill", ["-USR1", "-x", "vde-osk"]) { UseShellExecute = false });
            signal?.WaitForExit();
            if (signal is { ExitCode: 0 }) return;
            using var started = Process.Start(new ProcessStartInfo("vde-osk", ["--show"]) { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("vexplore: cannot run the on-screen keyboard (vde-osk): " + e.Message);
        }
    }
}
