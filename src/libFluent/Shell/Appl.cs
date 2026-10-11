public interface IAppLauncher : IDBusInterface
{
    Task LaunchAsync(string appId, string[] args);
    Task<IEnumerable<AppInfo>> GetInstalledAppsAsync();
    Task<IEnumerable<AppInfo>> SearchAppsAsync(string query);
}
