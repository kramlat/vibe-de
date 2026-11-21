namespace Vde.Shell.Notifications;

// What the notification centre shows: everything still waiting for the user (on screen or kept after its banner),
// newest first, grouped by application. A group shows its newest notification; "+N notifications" expands it.
public sealed record NotificationGroup(string AppName, string AppIcon, IReadOnlyList<Notification> Items, bool Expanded)
{
    public Notification Newest => Items[0];
    public int Hidden => Expanded ? 0 : Items.Count - 1;   // the "+N notifications" count
    public IReadOnlyList<Notification> Shown => Expanded ? Items : Items.Take(1).ToList();
}

public sealed class NotificationCenterModel : IDisposable
{
    private readonly NotificationService _service;
    private readonly HashSet<string> _expanded = [];

    public NotificationCenterModel(NotificationService service)
    {
        _service = service;
        service.Added += OnNotification;
        service.Updated += OnNotification;
        service.Closed += OnClosed;
        service.HistoryChanged += OnChange;
    }

    public void Dispose()
    {
        _service.Added -= OnNotification;
        _service.Updated -= OnNotification;
        _service.Closed -= OnClosed;
        _service.HistoryChanged -= OnChange;
    }

    // Raised on whichever thread changed the service: a view hops to its own.
    public event EventHandler? Changed;

    private void OnNotification(object? sender, Notification n) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnClosed(object? sender, NotificationClosedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnChange(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<NotificationGroup> Groups()
    {
        var all = _service.Active.Concat(_service.History)
            .GroupBy(n => n.Id).Select(g => g.First())            // an active one is also kept once it closes: show it once
            .OrderByDescending(n => n.Updated).ToList();
        return all.GroupBy(n => n.AppName)                        // groups ordered by their newest member (GroupBy keeps first-seen order)
            .Select(g => new NotificationGroup(g.Key, g.Select(n => n.AppIcon).FirstOrDefault(i => i.Length > 0) ?? "", g.ToList(), _expanded.Contains(g.Key)))
            .ToList();
    }

    public int Count => Groups().Sum(g => g.Items.Count);

    public bool DoNotDisturb
    {
        get => _service.Settings.DoNotDisturb;
        set { _service.Settings = _service.Settings with { DoNotDisturb = value }; Changed?.Invoke(this, EventArgs.Empty); }
    }

    public void ToggleExpanded(string appName)
    {
        if (!_expanded.Remove(appName))
            _expanded.Add(appName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss(uint id) => _service.Dismiss(id);

    public void DismissGroup(string appName)
    {
        foreach (var n in Groups().Where(g => g.AppName == appName).SelectMany(g => g.Items))
            _service.Dismiss(n.Id);
    }

    public void ClearAll()
    {
        foreach (var n in _service.Active)
            _service.Close(n.Id, CloseReason.Dismissed);
        _service.ClearHistory();
    }

    public void Invoke(Notification n, string key) => _service.InvokeAction(n.Id, key);

    public void Activate(Notification n) => _service.InvokeAction(n.Id, NotificationAction.Default);
}
