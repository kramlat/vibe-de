using Vibe.Registry;

namespace Vde.Shell.Notifications;

public interface IScheduler
{
    // Runs action once after delay. Disposing the result cancels it.
    IDisposable After(TimeSpan delay, Action action);
}

public sealed class TimerScheduler : IScheduler
{
    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

// What an application asks for with Notify: the arguments of the D-Bus call, with the hints already read.
public sealed record NotificationRequest(
    string AppName,
    string Summary,
    uint ReplacesId = 0,
    string AppIcon = "",
    string Body = "",
    IReadOnlyList<NotificationAction>? Actions = null,
    int ExpireTimeoutMs = -1,                // -1: the server decides, 0: never, otherwise milliseconds
    Urgency Urgency = Urgency.Normal,
    bool Resident = false,
    bool Transient = false,
    string? Category = null,
    string? DesktopEntry = null,
    string? ImagePath = null);

public sealed record NotificationSettings
{
    public bool DoNotDisturb { get; init; }
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxActive { get; init; } = 100;        // on screen or waiting in the list, all applications together
    public int MaxPerApp { get; init; } = 20;         // one noisy application cannot push everyone else out
    public int HistoryLimit { get; init; } = 200;
}

// Holds the notifications: what is showing, what was shown, and the rules between them. It knows nothing of
// D-Bus or of how a notification is drawn; the D-Bus server feeds it, and the shell UI listens to it.
// Thread-safe; events are raised on the thread that caused them, after the internal lock is released.
public sealed class NotificationService
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);
    private const int MaxActions = 8;

    private sealed class Entry(Notification notification)
    {
        public Notification Notification = notification;
        public IDisposable? Expiry;
    }

    private readonly object _gate = new();
    private readonly List<Entry> _active = new();           // oldest first
    private readonly List<Notification> _history = new();   // newest first
    private readonly IScheduler _scheduler;
    private readonly Func<DateTime> _clock;
    private NotificationSettings _settings;
    private uint _lastId;

    public NotificationService(IScheduler? scheduler = null, Func<DateTime>? clock = null, NotificationSettings? settings = null)
    {
        _scheduler = scheduler ?? new TimerScheduler();
        _clock = clock ?? (() => DateTime.UtcNow);
        _settings = settings ?? new NotificationSettings();
    }

    public event EventHandler<Notification>? Added;
    public event EventHandler<Notification>? Updated;
    public event EventHandler<NotificationClosedEventArgs>? Closed;
    public event EventHandler<ActionInvokedEventArgs>? ActionInvoked;

    public NotificationSettings Settings
    {
        get { lock (_gate) return _settings; }
        set { lock (_gate) _settings = value; }
    }

    public IReadOnlyList<Notification> Active
    {
        get { lock (_gate) return _active.Select(e => e.Notification).ToList(); }
    }

    public IReadOnlyList<Notification> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    // Returns the id: the replaced notification's own when it is still active, otherwise a new one.
    public uint Notify(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raised = new List<Action>();
        uint id;
        lock (_gate)
        {
            var now = _clock();
            var existing = request.ReplacesId == 0 ? null : _active.FirstOrDefault(e => e.Notification.Id == request.ReplacesId);

            // Make room before adding: one application cannot crowd out the others.
            if (existing is null)
            {
                while (_active.Count(e => e.Notification.AppName == request.AppName) >= _settings.MaxPerApp)
                    EvictOldest(e => e.Notification.AppName == request.AppName, raised);
                while (_active.Count >= _settings.MaxActive)
                    EvictOldest(e => e.Notification.Urgency != Urgency.Critical, raised, orAnything: true);
            }

            bool silenced = _settings.DoNotDisturb && request.Urgency != Urgency.Critical;
            id = existing?.Notification.Id ?? NextId();
            var notification = new Notification
            {
                Id = id,
                AppName = NotificationMarkup.Limit(request.AppName, 128),
                AppIcon = NotificationMarkup.Limit(request.AppIcon, 1024),
                Summary = NotificationMarkup.Limit(request.Summary, NotificationMarkup.MaxSummary),
                Body = NotificationMarkup.Limit(request.Body, NotificationMarkup.MaxBody),
                Actions = CleanActions(request.Actions),
                Urgency = request.Urgency,
                Timeout = silenced ? null : ResolveTimeout(request),
                Resident = request.Resident,
                Transient = request.Transient,
                Category = request.Category,
                DesktopEntry = request.DesktopEntry,
                ImagePath = request.ImagePath,
                Created = existing?.Notification.Created ?? now,
                Updated = now,
                Silenced = silenced,
            };

            Entry entry;
            if (existing is not null)
            {
                entry = existing;
                entry.Expiry?.Dispose();   // a replacement starts its time again
                entry.Notification = notification;
                raised.Add(() => Updated?.Invoke(this, notification));
            }
            else
            {
                entry = new Entry(notification);
                _active.Add(entry);
                raised.Add(() => Added?.Invoke(this, notification));
            }

            if (notification.Timeout is { } timeout)
                entry.Expiry = _scheduler.After(timeout, () => Close(id, CloseReason.Expired));
        }

        foreach (var raise in raised)
            raise();
        return id;
    }

    // False when there is no such active notification (already gone is not an error for the caller).
    public bool Close(uint id, CloseReason reason)
    {
        var raised = new List<Action>();
        lock (_gate)
        {
            var entry = _active.FirstOrDefault(e => e.Notification.Id == id);
            if (entry is null)
                return false;
            Remove(entry, reason, raised);
        }
        foreach (var raise in raised)
            raise();
        return true;
    }

    public void CloseAll(CloseReason reason)
    {
        var raised = new List<Action>();
        lock (_gate)
            foreach (var entry in _active.ToList())
                Remove(entry, reason, raised);
        foreach (var raise in raised)
            raise();
    }

    // The user (or a UI test) clicked an action. Unknown ids and keys are ignored. Unless the notification is
    // resident, invoking an action also dismisses it.
    public bool InvokeAction(uint id, string key)
    {
        Notification notification;
        lock (_gate)
        {
            var entry = _active.FirstOrDefault(e => e.Notification.Id == id);
            if (entry is null || !entry.Notification.Actions.Any(a => a.Key == key))
                return false;
            notification = entry.Notification;
        }

        ActionInvoked?.Invoke(this, new ActionInvokedEventArgs(id, key));
        if (!notification.Resident)
            Close(id, CloseReason.Dismissed);
        return true;
    }

    public void ClearHistory()
    {
        lock (_gate)
            _history.Clear();
    }

    // Keeps the settings in step with the registry (HKCU\Software\Vde\Notifications), now and whenever they change.
    public IDisposable BindTo(IHive hive)
    {
        Settings = NotificationSettingsStore.Load(hive);

        void OnChanged(object? sender, RegistryChange change)
        {
            if (change.IsUnder(NotificationSettingsStore.KeyPath))
                Settings = NotificationSettingsStore.Load(hive);
        }

        hive.Changed += OnChanged;
        return new Unsubscribe(() => hive.Changed -= OnChanged);
    }

    // ---- internals (caller holds the lock) ----

    private uint NextId()
    {
        do
        {
            _lastId = _lastId == uint.MaxValue ? 1 : _lastId + 1;   // 0 means "no replacement" in the protocol
        }
        while (_active.Any(e => e.Notification.Id == _lastId));
        return _lastId;
    }

    private TimeSpan? ResolveTimeout(NotificationRequest request)
    {
        if (request.ExpireTimeoutMs == 0)
            return null;
        if (request.ExpireTimeoutMs < 0)
            return request.Urgency == Urgency.Critical ? null : _settings.DefaultTimeout;   // critical stays until dealt with
        return Clamp(TimeSpan.FromMilliseconds(request.ExpireTimeoutMs));
    }

    private static TimeSpan Clamp(TimeSpan value) => value < MinTimeout ? MinTimeout : value > MaxTimeout ? MaxTimeout : value;

    private static IReadOnlyList<NotificationAction> CleanActions(IReadOnlyList<NotificationAction>? actions)
    {
        var clean = new List<NotificationAction>();
        foreach (var action in actions ?? [])
        {
            if (action.Key.Length == 0 || clean.Count >= MaxActions || clean.Any(a => a.Key == action.Key))
                continue;
            clean.Add(new NotificationAction(NotificationMarkup.Limit(action.Key, 64), NotificationMarkup.Limit(action.Label, 128)));
        }
        return clean;
    }

    private void EvictOldest(Func<Entry, bool> preferred, List<Action> raised, bool orAnything = false)
    {
        var victim = _active.FirstOrDefault(preferred) ?? (orAnything ? _active.FirstOrDefault() : null);
        if (victim is not null)
            Remove(victim, CloseReason.Undefined, raised);
    }

    private void Remove(Entry entry, CloseReason reason, List<Action> raised)
    {
        entry.Expiry?.Dispose();
        _active.Remove(entry);
        var notification = entry.Notification;
        if (!notification.Transient)
        {
            _history.Insert(0, notification);
            if (_history.Count > _settings.HistoryLimit)
                _history.RemoveRange(_settings.HistoryLimit, _history.Count - _settings.HistoryLimit);
        }
        raised.Add(() => Closed?.Invoke(this, new NotificationClosedEventArgs(notification.Id, reason, notification)));
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public sealed class NotificationClosedEventArgs(uint id, CloseReason reason, Notification notification) : EventArgs
{
    public uint Id { get; } = id;
    public CloseReason Reason { get; } = reason;
    public Notification Notification { get; } = notification;
}

public sealed class ActionInvokedEventArgs(uint id, string key) : EventArgs
{
    public uint Id { get; } = id;
    public string Key { get; } = key;
}

// Notification settings live in the registry so Settings, the shell and scripts all see the same ones.
public static class NotificationSettingsStore
{
    public const string KeyPath = @"Software\Vde\Notifications";

    public static NotificationSettings Load(IHive hive)
    {
        var defaults = new NotificationSettings();
        return new NotificationSettings
        {
            DoNotDisturb = Flag(hive, "DoNotDisturb") ?? defaults.DoNotDisturb,
            DefaultTimeout = Number(hive, "DefaultTimeoutMs") is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 500, 600_000)) : defaults.DefaultTimeout,
            MaxActive = Number(hive, "MaxActive") is { } active ? Math.Clamp((int)active, 1, 1000) : defaults.MaxActive,
            MaxPerApp = Number(hive, "MaxPerApp") is { } perApp ? Math.Clamp((int)perApp, 1, 1000) : defaults.MaxPerApp,
            HistoryLimit = Number(hive, "HistoryLimit") is { } history ? Math.Clamp((int)history, 0, 10_000) : defaults.HistoryLimit,
        };
    }

    public static void Save(IHive hive, NotificationSettings settings)
    {
        hive.SetValue(KeyPath, "DoNotDisturb", RegistryValueKind.DWord, settings.DoNotDisturb ? 1u : 0u);
        hive.SetValue(KeyPath, "DefaultTimeoutMs", RegistryValueKind.DWord, (uint)settings.DefaultTimeout.TotalMilliseconds);
        hive.SetValue(KeyPath, "MaxActive", RegistryValueKind.DWord, (uint)settings.MaxActive);
        hive.SetValue(KeyPath, "MaxPerApp", RegistryValueKind.DWord, (uint)settings.MaxPerApp);
        hive.SetValue(KeyPath, "HistoryLimit", RegistryValueKind.DWord, (uint)settings.HistoryLimit);
    }

    // A missing or wrongly typed value means "the default": a bad registry entry must not break notifications.
    private static long? Number(IHive hive, string name) => hive.GetValue(KeyPath, name) switch
    {
        { Kind: RegistryValueKind.DWord, Data: uint d } => d,
        { Kind: RegistryValueKind.Int32, Data: int i } => i,
        _ => null,
    };

    private static bool? Flag(IHive hive, string name) => hive.GetValue(KeyPath, name) switch
    {
        { Kind: RegistryValueKind.Bool, Data: bool b } => b,
        { Kind: RegistryValueKind.DWord, Data: uint d } => d != 0,
        _ => null,
    };
}
