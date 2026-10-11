public interface ISessionManager : IDBusInterface
{
    Task LockAsync();
    Task UnlockAsync();
    Task LogoutAsync();
    Task SwitchUserAsync();
    Task SuspendAsync();
    Task HibernateAsync();
    Task ShutdownAsync();
    Task RebootAsync();
}
