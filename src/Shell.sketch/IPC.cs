public interface IDBusInterface
{
    // The D-Bus service name, e.g. "com.fluent.WindowManager"
    string ServiceName { get; }

    // The D-Bus object path, e.g. "/com/fluent/WindowManager"
    string ObjectPath { get; }

    // Human-readable name for debugging and introspection
    string DisplayName { get; }

    // Called when the service is registered with the bus
    Task InitializeAsync();

    // Called when the service is being shut down
    Task ShutdownAsync();

    // Optional: metadata for introspection, versioning, capabilities
    Task<Dictionary<string,string>> GetMetadataAsync();
}
