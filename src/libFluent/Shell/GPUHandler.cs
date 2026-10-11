public interface IAbstractGraphicsAccelerator : IDBusInterface
{
    string AcceleratorId { get; }
    string DisplayName { get; }

    Task<GraphicsContextHandle> CreateContextAsync(GraphicsContextRequest request);
    Task DestroyContextAsync(string contextId);

    Task<AcceleratorInfo> GetInfoAsync();
}
