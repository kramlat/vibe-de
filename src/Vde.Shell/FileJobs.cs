using System.Globalization;

namespace Vde.Shell;

// What a copy or move looks like from the outside, the way the transfer window shows it: how far along, how
// fast, how long to go, what is being copied now. Fed with the operation's progress reports (any thread) and
// sampled on a timer for the speed graph. No user interface in here.
public sealed class TransferMonitor
{
    public const int MaxSamples = 400;

    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly List<(double Fraction, double BytesPerSecond)> _history = [];
    private DateTime _started, _lastSample;
    private long _bytesAtLastSample;
    private double _smoothed;
    private FileProgress _last = new("", 0, 0, 0, 0);
    private bool _paused, _finished;

    public TransferMonitor(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _started = _lastSample = _clock();
    }

    public void Report(FileProgress progress)
    {
        lock (_lock) _last = progress;
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
            if (paused) _smoothed = 0;
            _lastSample = _clock();               // time spent paused is not part of the next speed sample
            _bytesAtLastSample = _last.BytesDone;
        }
    }

    public void Finish()
    {
        lock (_lock) _finished = true;
    }

    // Takes one speed sample from what was copied since the last one. Call a few times a second.
    public void Tick()
    {
        lock (_lock)
        {
            var now = _clock();
            var seconds = (now - _lastSample).TotalSeconds;
            if (seconds <= 0.001) return;
            double speed = _paused ? 0 : Math.Max(0, (_last.BytesDone - _bytesAtLastSample) / seconds);
            _smoothed = _smoothed == 0 ? speed : _smoothed * 0.7 + speed * 0.3;   // the estimate must not jump with every block
            _bytesAtLastSample = _last.BytesDone;
            _lastSample = now;
            _history.Add((Fraction, speed));
            if (_history.Count > MaxSamples) _history.RemoveRange(0, _history.Count - MaxSamples);
        }
    }

    public double Fraction
    {
        get
        {
            lock (_lock)
            {
                if (_finished) return 1;
                if (_last.BytesTotal > 0) return Math.Clamp((double)_last.BytesDone / _last.BytesTotal, 0, 1);
                return _last.ItemsTotal > 0 ? Math.Clamp((double)_last.ItemsDone / _last.ItemsTotal, 0, 1) : 0;   // only empty files and folders
            }
        }
    }

    public int Percent => (int)Math.Floor(Fraction * 100);
    public string CurrentName { get { lock (_lock) return Path.GetFileName(_last.CurrentPath.TrimEnd('/')); } }
    public int ItemsRemaining { get { lock (_lock) return Math.Max(0, _last.ItemsTotal - _last.ItemsDone); } }
    public long BytesRemaining { get { lock (_lock) return Math.Max(0, _last.BytesTotal - _last.BytesDone); } }
    public long BytesTotal { get { lock (_lock) return _last.BytesTotal; } }
    public double BytesPerSecond { get { lock (_lock) return _smoothed; } }
    public bool IsPaused { get { lock (_lock) return _paused; } }

    // The speed samples against how far along the copy was when each was taken (the graph's x axis is progress).
    public IReadOnlyList<(double Fraction, double BytesPerSecond)> History { get { lock (_lock) return _history.ToList(); } }

    // Null until there has been something to measure (and while paused): the window then says "Calculating...".
    public TimeSpan? Remaining
    {
        get
        {
            lock (_lock)
            {
                if (_paused || _smoothed < 1 || (_clock() - _started).TotalSeconds < 1) return null;
                return TimeSpan.FromSeconds(Math.Max(0, (_last.BytesTotal - _last.BytesDone) / _smoothed));
            }
        }
    }
}

// The words of the transfer window, in one place.
public static class TransferText
{
    public static string Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } r) return "Calculating...";
        if (r.TotalSeconds < 1) return "Less than a second";
        int seconds = (int)Math.Ceiling(r.TotalSeconds);
        if (seconds < 60) return $"About {(seconds > 10 ? (seconds + 2) / 5 * 5 : seconds)} {(seconds == 1 ? "second" : "seconds")}";
        int minutes = (int)Math.Round(r.TotalMinutes);
        if (minutes < 60) return $"About {minutes} {(minutes == 1 ? "minute" : "minutes")}";
        int hours = minutes / 60, rest = minutes % 60;
        return rest == 0 ? $"About {hours} {(hours == 1 ? "hour" : "hours")}" : $"About {hours} {(hours == 1 ? "hour" : "hours")} {rest} {(rest == 1 ? "minute" : "minutes")}";
    }

    public static string Speed(double bytesPerSecond) => ByteFormat.Format((long)bytesPerSecond) + "/s";

    public static string Verb(bool move) => move ? "Moving" : "Copying";

    public static string Items(int count) => count == 1 ? "1 item" : count.ToString(CultureInfo.CurrentCulture) + " items";

    // "6 (1.58 GB)"
    public static string ItemsRemaining(int items, long bytes) => bytes > 0 ? $"{items} ({ByteFormat.Format(bytes)})" : items.ToString(CultureInfo.CurrentCulture);

    public static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : Path.GetFileName(trimmed);
    }
}

public enum JobState { Running, Paused, Completed, Cancelled, Failed }

// A copy or move running on a worker thread, with what a transfer window needs: progress, pause, cancel, the
// outcome. The caller awaits Completion; the file manager shows the window if it takes longer than a moment.
public sealed class FileJob : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly PauseGate _gate = new();
    private readonly Timer _timer;

    private FileJob(bool move, IReadOnlyList<string> sources, string destination, Func<DateTime>? clock)
    {
        Move = move;
        Sources = sources;
        Destination = destination;
        Monitor = new TransferMonitor(clock);
        _timer = new Timer(_ => Monitor.Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public bool Move { get; }
    public IReadOnlyList<string> Sources { get; }
    public string Destination { get; }
    public TransferMonitor Monitor { get; }
    public JobState State { get; private set; } = JobState.Running;
    public Exception? Error { get; private set; }
    public IReadOnlyList<string> Created { get; private set; } = [];
    public Task Completion { get; private set; } = Task.CompletedTask;
    public int ItemCount => Sources.Count;

    // The folder the items come from, as the window says it ("from Captures").
    public string SourceFolder => Sources.Count == 0 ? "" : Path.GetDirectoryName(Sources[0].TrimEnd('/')) ?? "/";

    // The two ways to start one: the work function gets options carrying cancel, pause and progress.
    public static FileJob Start(bool move, IReadOnlyList<string> sources, string destination,
        Func<FileOperationOptions, IReadOnlyList<string>> work, ConflictResolver? conflicts = null, Func<DateTime>? clock = null)
    {
        var job = new FileJob(move, sources, destination, clock);
        var options = new FileOperationOptions
        {
            OnConflict = conflicts,
            Cancel = job._cancel.Token,
            Pause = job._gate,
            Progress = new Reporter(job.Monitor),
        };
        job.Completion = Task.Run(() =>
        {
            try
            {
                job.Created = work(options);
                job.State = JobState.Completed;
            }
            catch (OperationCanceledException) { job.State = JobState.Cancelled; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                job.Error = e;
                job.State = JobState.Failed;
            }
            finally
            {
                job.Monitor.Finish();
                job._timer.Dispose();
            }
        });
        return job;
    }

    public void Pause()
    {
        if (State != JobState.Running) return;
        _gate.Pause();
        Monitor.SetPaused(true);
        State = JobState.Paused;
    }

    public void Resume()
    {
        if (State != JobState.Paused) return;
        Monitor.SetPaused(false);
        _gate.Resume();
        State = JobState.Running;
    }

    public void Cancel() => _cancel.Cancel();

    public void Dispose()
    {
        _timer.Dispose();
        _cancel.Dispose();
    }

    private sealed class Reporter(TransferMonitor monitor) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => monitor.Report(value);   // called on the worker; the monitor is thread safe
    }
}
