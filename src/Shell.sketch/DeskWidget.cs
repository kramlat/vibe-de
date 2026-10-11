public interface IDesktopWidget : IDBusInterface
{
    string WidgetId { get; }
    string DisplayName { get; }
    string Icon { get; }

    Task<object> CreateViewAsync();
    Task<object?> CreateConfigPanelAsync();
}
