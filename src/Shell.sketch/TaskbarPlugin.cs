public interface ITaskbarPlugin : IDBusInterface
{
    string PluginId { get; }
    string DisplayName { get; }
    string Icon { get; }

    Task<object> CreateTaskbarItemAsync();
    Task<object?> CreateConfigPanelAsync();
}
