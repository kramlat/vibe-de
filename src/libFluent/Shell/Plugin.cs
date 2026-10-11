public interface IPlugin : IDBusInterface
{
    string PluginId { get; }
    string DisplayName { get; }
    Task InitializeAsync();
    Task ShutdownAsync();
}
