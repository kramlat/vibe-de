namespace Vibe.Registry;

// What a hive offers, wherever it lives: a file this process has loaded (Hive) or a daemon on a socket
// (RemoteHive). Everything that uses the registry (HKCR, themes, file types, components) takes this, so it
// does not care which. Paths use '\' and are case-insensitive; the empty path is the root key.
// Denied calls throw UnauthorizedAccessException; a missing key where one is required throws KeyNotFoundException.
public interface IHive
{
    string Name { get; }

    // Raised when something changes, including changes made by other processes.
    event EventHandler<RegistryChange>? Changed;

    bool KeyExists(string path);
    void CreateKey(string path);
    bool DeleteKey(string path);
    IReadOnlyList<string> GetSubKeyNames(string path);
    RegistryKeyInfo GetKeyInfo(string path);
    void SetKeyFlags(string path, RegistryKeyFlags flags);
    PosixPermissions GetKeyPermissions(string path);
    void SetKeyPermissions(string path, PosixPermissions permissions);

    RegistryValue? GetValue(string path, string name);
    IReadOnlyList<RegistryValue> GetValues(string path);
    PosixPermissions? GetValuePermissions(string path, string name);
    void SetValue(string path, string name, RegistryValueKind kind, object? data);
    void SetValuePermissions(string path, string name, PosixPermissions permissions);
    bool DeleteValue(string path, string name);
}
