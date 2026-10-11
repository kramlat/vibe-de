public interface ISettingsProvider : IDBusInterface
{
    Task<object?> GetValueAsync(string keyPath, string valueName);
    Task SetValueAsync(string keyPath, string valueName, object? value);
    Task DeleteValueAsync(string keyPath, string valueName);
}
