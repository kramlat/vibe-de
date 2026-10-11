using Vde.Freedesktop;

namespace Vde.Shell;

public enum KnownFolder
{
    Home,
    Desktop,
    Documents,
    Downloads,
    Music,
    Pictures,
    Videos,
    Templates,
    PublicShare,
    Applications,   // user's own .desktop files
    Config,
    Data,
    Cache,
}

public sealed class KnownFolders
{
    private readonly XdgDirectories _xdg;

    public KnownFolders(XdgDirectories? xdg = null) => _xdg = xdg ?? XdgDirectories.Default;

    public string Get(KnownFolder folder) => folder switch
    {
        KnownFolder.Home => _xdg.Home,
        KnownFolder.Desktop => _xdg.UserDir("DESKTOP"),
        KnownFolder.Documents => _xdg.UserDir("DOCUMENTS"),
        KnownFolder.Downloads => _xdg.UserDir("DOWNLOAD"),
        KnownFolder.Music => _xdg.UserDir("MUSIC"),
        KnownFolder.Pictures => _xdg.UserDir("PICTURES"),
        KnownFolder.Videos => _xdg.UserDir("VIDEOS"),
        KnownFolder.Templates => _xdg.UserDir("TEMPLATES"),
        KnownFolder.PublicShare => _xdg.UserDir("PUBLICSHARE"),
        KnownFolder.Applications => Path.Combine(_xdg.DataHome, "applications"),
        KnownFolder.Config => _xdg.ConfigHome,
        KnownFolder.Data => _xdg.DataHome,
        KnownFolder.Cache => _xdg.CacheHome,
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };
}
