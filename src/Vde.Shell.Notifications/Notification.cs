namespace Vde.Shell.Notifications;

public enum Urgency
{
    Low = 0,
    Normal = 1,
    Critical = 2,
}

// The numbers are the ones the Desktop Notifications spec sends in NotificationClosed.
public enum CloseReason : uint
{
    Expired = 1,
    Dismissed = 2,      // the user closed it
    ClosedByCall = 3,   // the application called CloseNotification
    Undefined = 4,      // anything else, e.g. pushed out by newer ones
}

public sealed record NotificationAction(string Key, string Label)
{
    // Clicking the notification itself invokes the action with this key, if the application provided one.
    public const string Default = "default";
}

public sealed record Notification
{
    public required uint Id { get; init; }
    public required string AppName { get; init; }
    public string AppIcon { get; init; } = string.Empty;       // a freedesktop icon name or a file path
    public required string Summary { get; init; }
    public string Body { get; init; } = string.Empty;          // may contain the spec's small markup subset
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];
    public Urgency Urgency { get; init; } = Urgency.Normal;

    // How long it stays on screen; null means until it is closed. Critical notifications never expire by themselves.
    public TimeSpan? Timeout { get; init; }

    public bool Resident { get; init; }       // stays after an action was invoked
    public bool Transient { get; init; }      // never kept in the history
    public string? Category { get; init; }
    public string? DesktopEntry { get; init; }
    public string? ImagePath { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }

    // True when the user was not interrupted for it (do not disturb): it is in the list but did not pop up.
    public bool Silenced { get; init; }

    public string PlainBody => NotificationMarkup.ToPlainText(Body);
}
