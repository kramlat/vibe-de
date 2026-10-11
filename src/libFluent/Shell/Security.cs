public interface IHelloAuthService : IDBusInterface
{
    Task<AuthResult> AuthenticateAsync(AuthRequest request);
    Task<bool> IsAvailableAsync(AuthFactor factor);
    Task RegisterFactorAsync(AuthFactor factor);
    Task RemoveFactorAsync(AuthFactor factor);
}
