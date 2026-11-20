using System.Diagnostics;

namespace Vde.Shell.Controls;

// With VDE_TRACE=1 in the environment, the shell's programs say on stderr how long after their process began
// each step of starting happened. A slow window is then a list of numbers instead of a guess:
//   VDE_TRACE=1 vexplore --files 2>&1 | grep trace
public static class StartupTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("VDE_TRACE") is "1" or "true";

    // VDE_TRACE_EXIT=1 ends the program after its first layout: for timing it from a script with `time`
    // (the CPU seconds stay meaningful when the machine is busy, the wall clock does not).
    public static void ExitIfAsked()
    {
        if (Enabled && Environment.GetEnvironmentVariable("VDE_TRACE_EXIT") is "1")
            Environment.Exit(0);
    }

    // VDE_TRACE_SHOT=file.png writes a picture of the window's visual tree two seconds after its first layout
    // (drawn on the CPU from the tree, so it shows what the controls hold, whatever the GPU path then does).
    public static void ShotIfAsked(Avalonia.Controls.Control window)
    {
        if (Environment.GetEnvironmentVariable("VDE_TRACE_SHOT") is not { Length: > 0 } file) return;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var size = new Avalonia.PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bitmap.Render(window);
            bitmap.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.Error.WriteLine("trace shot written " + file);
            if (Environment.GetEnvironmentVariable("VDE_TRACE_EXIT") is "1") Environment.Exit(0);
        };
        timer.Start();
    }

    private static long _last;

    public static void Mark(string what)
    {
        if (!Enabled) return;
        var since = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var now = Stopwatch.GetTimestamp();
        var step = _last == 0 ? 0 : (now - _last) * 1000.0 / Stopwatch.Frequency;
        _last = now;
        Console.Error.WriteLine($"trace {since,7:0} ms  (+{step,6:0})  {what}");
    }
}
