public interface IRootDesktopSurface : IDBusInterface
{
    // Current wallpaper info
    Task<WallpaperInfo> GetCurrentAsync();

    // Set wallpaper (image, video, shader, color)
    Task SetWallpaperAsync(WallpaperRequest request);

    // Per-monitor wallpaper
    Task SetWallpaperForMonitorAsync(string monitorId, WallpaperRequest request);

    // Transition control
    Task SetTransitionAsync(TransitionStyle style, TimeSpan duration);

    // Surface export to compositor
    Task<UnixFd> GetSurfaceHandleAsync();

    // Accent color extraction
    Task<Color> GetDominantColorAsync();
    Task<Color[]> GetPaletteAsync();
}

public sealed class WallpaperRequest
{
    public WallpaperType Type { get; set; }
    public string Source { get; set; } = ""; // file path, URL, shader ID
    public WallpaperFit Fit { get; set; } = WallpaperFit.Cover;
    public WallpaperAlignment Alignment { get; set; } = WallpaperAlignment.Center;
}
