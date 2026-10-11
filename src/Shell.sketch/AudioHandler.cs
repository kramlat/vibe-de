public interface IAbstractAudioHandler : IDBusInterface
{
    string HandlerId { get; }
    string DisplayName { get; }

    Task PlayAsync(AudioRequest request);
    Task StopAsync(string streamId);
    Task SetVolumeAsync(string streamId, double volume);
    Task SetMuteAsync(string streamId, bool mute);

    Task<IEnumerable<AudioDeviceInfo>> GetDevicesAsync();
    Task SetDefaultDeviceAsync(string deviceId);

    Task<object?> CreateConfigPanelAsync();
}
