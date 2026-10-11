public interface IAbstractVideoHandler : IDBusInterface
{
    string HandlerId { get; }
    string DisplayName { get; }

    Task<VideoStreamHandle> OpenStreamAsync(VideoStreamRequest request);
    Task CloseStreamAsync(string streamId);

    Task<object?> CreateConfigPanelAsync();
}
