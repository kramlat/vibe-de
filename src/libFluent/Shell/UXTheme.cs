public interface IThemeService : IDBusInterface
{
    Task SetThemeAsync(ThemeRequest request);
    Task<ThemeInfo> GetCurrentThemeAsync();
    Task SetAccentColorAsync(Color color);
}
