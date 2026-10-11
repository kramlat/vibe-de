public interface INetworkManager : IDBusInterface
{
    Task<IEnumerable<NetworkAdapterInfo>> GetAdaptersAsync();
    Task ConnectAsync(string ssid, string password);
    Task DisconnectAsync(string ssid);
    Task EnableAdapterAsync(string adapterId);
    Task DisableAdapterAsync(string adapterId);
}
