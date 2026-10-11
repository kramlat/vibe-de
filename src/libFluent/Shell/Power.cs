public interface IPowerManager : IDBusInterface
{
    Task<double> GetBatteryLevelAsync();
    Task<bool> IsChargingAsync();
    Task SetBrightnessAsync(double level);
    Task SetPowerProfileAsync(PowerProfile profile);
}
