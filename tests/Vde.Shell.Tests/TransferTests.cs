using Vde.Shell;

namespace Vde.Shell.Tests;

public class TransferTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void The_monitor_turns_progress_reports_into_percent_speed_time_left_and_a_history()
    {
        var clock = new Clock();
        var m = new TransferMonitor(() => clock.Now);
        Assert.Equal("Calculating...", TransferText.Remaining(m.Remaining));

        m.Report(new FileProgress("/a/video file 2.mp4", 100, 1000, 1, 8));
        clock.Advance(1);
        m.Tick();                                             // 100 bytes in a second

        Assert.Equal(10, m.Percent);
        Assert.Equal("video file 2.mp4", m.CurrentName);
        Assert.Equal(7, m.ItemsRemaining);
        Assert.Equal(900, m.BytesRemaining);
        Assert.Equal(100, m.BytesPerSecond, 3);
        Assert.Equal(9, m.Remaining!.Value.TotalSeconds, 3);   // 900 bytes at 100 B/s
        var sample = Assert.Single(m.History);
        Assert.Equal(0.1, sample.Fraction, 3);
        Assert.Equal(100, sample.BytesPerSecond, 3);

        m.Report(new FileProgress("/a/b", 600, 1000, 3, 8));
        clock.Advance(1);
        m.Tick();                                             // 500 B/s now, but the estimate eases toward it
        Assert.InRange(m.BytesPerSecond, 100, 500);
        Assert.Equal(2, m.History.Count);
        Assert.Equal(500, m.History[1].BytesPerSecond, 3);    // the graph gets the raw sample
        m.Finish();
        Assert.Equal(100, m.Percent);
    }

    [Fact]
    public void Pausing_stops_the_speed_and_the_estimate_and_the_paused_time_is_not_counted_on_resume()
    {
        var clock = new Clock();
        var m = new TransferMonitor(() => clock.Now);
        m.Report(new FileProgress("x", 100, 1000, 0, 1));
        clock.Advance(1); m.Tick();

        m.SetPaused(true);
        clock.Advance(10); m.Tick();
        Assert.Equal(0, m.BytesPerSecond);
        Assert.Null(m.Remaining);

        m.SetPaused(false);
        m.Report(new FileProgress("x", 200, 1000, 0, 1));
        clock.Advance(1); m.Tick();
        Assert.Equal(100, m.History[^1].BytesPerSecond, 3);   // 100 bytes over one second, not over eleven
    }

    [Fact]
    public void A_job_of_only_empty_files_still_reports_progress_by_items()
    {
        var m = new TransferMonitor();
        m.Report(new FileProgress("a", 0, 0, 2, 4));
        Assert.Equal(50, m.Percent);
    }

    [Theory]
    [InlineData(0.2, "Less than a second")]
    [InlineData(1, "About 1 second")]
    [InlineData(5, "About 5 seconds")]
    [InlineData(12, "About 10 seconds")]
    [InlineData(48, "About 50 seconds")]
    [InlineData(120, "About 2 minutes")]
    [InlineData(3600 + 5 * 60, "About 1 hour 5 minutes")]
    [InlineData(7200, "About 2 hours")]
    public void The_time_left_is_said_the_way_Windows_says_it(double seconds, string expected)
        => Assert.Equal(expected, TransferText.Remaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_other_words_of_the_window()
    {
        Assert.Equal("Copying", TransferText.Verb(false));
        Assert.Equal("Moving", TransferText.Verb(true));
        Assert.Equal("1 item", TransferText.Items(1));
        Assert.Equal("8 items", TransferText.Items(8));
        Assert.Equal("6 (1.5 GB)", TransferText.ItemsRemaining(6, (long)(1.5 * 1024 * 1024 * 1024)).Replace("1.50", "1.5"));
        Assert.Equal("3", TransferText.ItemsRemaining(3, 0));
        Assert.Equal("Video clips", TransferText.FolderName("/home/u/Video clips/"));
        Assert.Equal("/", TransferText.FolderName("/"));
        Assert.EndsWith("/s", TransferText.Speed(158 * 1024));
    }

    [Fact]
    public async Task The_gate_holds_a_worker_until_it_is_resumed_and_a_cancel_wakes_it()
    {
        var gate = new PauseGate();
        gate.Pause();
        var passed = false;
        var worker = Task.Run(() => { gate.Wait(CancellationToken.None); passed = true; });
        await Task.Delay(300);
        Assert.False(passed);
        gate.Resume();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(passed);

        gate.Pause();
        using var cancel = new CancellationTokenSource();
        var blocked = Task.Run(() => gate.Wait(cancel.Token));
        await Task.Delay(150);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-job-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "src");
        public string Target => Path.Combine(Root, "dst");
        public Tree() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Target); }
        public string File_(string name, int megabytes)
        {
            var path = Path.Combine(Source, name);
            using var f = File.Create(path);
            f.Write(new byte[megabytes * 1024 * 1024]);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public async Task A_job_copies_on_a_worker_reports_the_end_and_names_where_from_and_to()
    {
        using var t = new Tree();
        var a = t.File_("a.bin", 2); var b = t.File_("b.bin", 1);
        var job = FileJob.Start(false, [a, b], t.Target, o => FileOperations.Copy([a, b], t.Target, o), (_, _) => ConflictAction.KeepBoth);

        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Created.Count);
        Assert.True(File.Exists(Path.Combine(t.Target, "a.bin")));
        Assert.Equal(100, job.Monitor.Percent);
        Assert.Equal(t.Source, job.SourceFolder);
        Assert.Equal(2, job.ItemCount);
    }

    [Fact]
    public async Task Pause_holds_the_copy_resume_lets_it_finish_and_cancel_leaves_no_half_file()
    {
        using var t = new Tree();
        var big = t.File_("big.bin", 192);
        var job = FileJob.Start(false, [big], t.Target, o => FileOperations.Copy([big], t.Target, o));
        job.Pause();
        Assert.Equal(JobState.Paused, job.State);
        await Task.Delay(300);
        var before = job.Monitor.Fraction;
        await Task.Delay(500);
        Assert.Equal(before, job.Monitor.Fraction);            // nothing moved while paused
        Assert.True(before < 1);
        Assert.False(job.Completion.IsCompleted);

        job.Resume();
        Assert.Equal(JobState.Running, job.State);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(192L * 1024 * 1024, new FileInfo(Path.Combine(t.Target, "big.bin")).Length);

        File.Delete(Path.Combine(t.Target, "big.bin"));
        var second = FileJob.Start(false, [big], t.Target, o => FileOperations.Copy([big], t.Target, o));
        second.Pause();
        second.Cancel();                                        // cancelling a paused job works too
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobState.Cancelled, second.State);
        Assert.False(File.Exists(Path.Combine(t.Target, "big.bin")));
    }

    [Fact]
    public async Task A_failing_job_says_why_instead_of_throwing()
    {
        using var t = new Tree();
        var job = FileJob.Start(true, ["/nonexistent/x"], t.Target, _ => throw new IOException("disk full"));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("disk full", job.Error!.Message);
    }
}
