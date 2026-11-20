using Avalonia.Headless;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vde.Shell;
using Vde.Shell.Controls;
using VExplore.Windows;

namespace VExplore.Tests;

public class TransferWindowTests
{
    // A job that reports a fixed state and then waits, so the window has something steady to show.
    private static (FileJob job, ManualResetEventSlim release) Held(Func<FileOperationOptions, IReadOnlyList<string>>? after = null)
    {
        var release = new ManualResetEventSlim();
        var job = FileJob.Start(false, ["/home/u/Captures/a.mp4", "/home/u/Captures/b.mp4"], "/home/u/Video clips", options =>
        {
            options.Progress!.Report(new FileProgress("/home/u/Captures/video file 2.mp4", 28, 100, 2, 8));
            while (!release.Wait(20)) { options.Pause!.Wait(options.Cancel); options.Cancel.ThrowIfCancellationRequested(); }
            return [];
        });
        return (job, release);
    }

    [AvaloniaFact]
    public void The_window_shows_the_percentage_in_its_title_and_in_the_headline_and_the_details()
    {
        var (job, release) = Held();
        var opened = new List<string>();
        var window = new TransferWindow(job, opened.Add);
        window.Show();

        Assert.Equal("28% complete", window.PercentText);
        Assert.Equal("28% complete", window.Title);
        Assert.Equal("video file 2.mp4", window.NameText);
        Assert.Equal("Calculating...", window.TimeText);
        Assert.Equal("6 (72 bytes)", window.ItemsText);               // eight items, two done; 28 of 100 bytes
        Assert.True(window.ShowsDetails);
        Assert.Equal(0.28, window.Graph.Progress, 2);

        var links = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.Tag is string).ToList();
        Assert.Equal(new[] { "Captures", "Video clips" }, links.Select(b => ((Avalonia.Controls.TextBlock)b.Content!).Text));
        links[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(new[] { "/home/u/Captures" }, opened);          // the folder names are links

        release.Set();
        job.Completion.Wait(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Fewer_details_folds_the_graph_away_and_pause_and_cancel_reach_the_job()
    {
        var (job, release) = Held();
        var window = new TransferWindow(job);
        window.Show();

        window.ToggleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.False(window.ShowsDetails);
        Assert.False(window.Graph.IsEffectivelyVisible);              // folded
        window.ToggleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(window.ShowsDetails);

        window.PauseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(JobState.Paused, job.State);
        window.PauseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(JobState.Running, job.State);

        window.Close();                                              // closing the window stops the copy
        job.Completion.Wait(TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Cancelled, job.State);
        release.Set();
    }

    [AvaloniaFact]
    public async Task The_window_only_appears_for_a_job_that_takes_a_while_and_closes_itself_at_the_end()
    {
        var quick = FileJob.Start(false, ["/a"], "/b", _ => []);
        await TransferWindow.ShowIfSlowAsync(quick, null, delay: TimeSpan.FromSeconds(2));   // done before the delay: no window
        Assert.Equal(JobState.Completed, quick.State);

        var (slow, release) = Held();
        var waiting = TransferWindow.ShowIfSlowAsync(slow, null, delay: TimeSpan.FromMilliseconds(100));
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        release.Set();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(JobState.Completed, slow.State);
    }

    [AvaloniaFact]
    public void Transfer_window_picture()
    {
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is not { Length: > 0 } dir) return;
        var job = FileJob.Start(false, Enumerable.Range(0, 8).Select(i => $"/home/u/Captures/c{i}.mp4").ToList(), "/home/u/Video clips", options =>
        {
            for (int i = 1; i <= 28; i++)
            {
                options.Progress!.Report(new FileProgress("/home/u/Captures/video file 2 - Copy.mp4", i * 40_000_000L, 4_000_000_000L * 1 / 1, 2, 8));
                Thread.Sleep(30);
            }
            Thread.Sleep(30_000);
            return [];
        });
        var window = new TransferWindow(job) { Width = 480 };
        window.Show();
        Thread.Sleep(1200);
        Dispatcher.UIThread.RunJobs();
        window.Refresh();
        window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "transfer.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        job.Cancel();
        window.Close();
    }
}
