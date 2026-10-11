public interface INotificationServer : IDBusInterface
{
    Task<string> ShowNotificationAsync(NotificationRequest request);
    Task DismissNotificationAsync(string notificationId);
    Task<IEnumerable<NotificationInfo>> GetActiveNotificationsAsync();
}
