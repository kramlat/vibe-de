namespace Vde.Freedesktop;

// XDG Base Directory spec, plus the user-dirs.dirs folders (Desktop, Documents, ...).
// Takes an environment lookup so it can be tested without touching the real environment.
public sealed class XdgDirectories
{
    private static readonly Dictionary<string, string> UserDirDefaults = new()
    {
        ["DESKTOP"] = "Desktop",
        ["DOWNLOAD"] = "Downloads",
        ["DOCUMENTS"] = "Documents",
        ["MUSIC"] = "Music",
        ["PICTURES"] = "Pictures",
        ["VIDEOS"] = "Videos",
        ["TEMPLATES"] = "Templates",
        ["PUBLICSHARE"] = "Public",
    };

    private readonly Func<string, string?> _env;
    private Dictionary<string, string>? _userDirs;

    public static XdgDirectories Default { get; } = new();

    public XdgDirectories(Func<string, string?>? env = null)
        => _env = env ?? Environment.GetEnvironmentVariable;

    public string Home => NonEmpty("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string DataHome => Absolute("XDG_DATA_HOME") ?? Path.Combine(Home, ".local", "share");
    public string ConfigHome => Absolute("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");
    public string CacheHome => Absolute("XDG_CACHE_HOME") ?? Path.Combine(Home, ".cache");
    public string StateHome => Absolute("XDG_STATE_HOME") ?? Path.Combine(Home, ".local", "state");
    public string? RuntimeDir => Absolute("XDG_RUNTIME_DIR");

    public IReadOnlyList<string> DataDirs => List("XDG_DATA_DIRS", ["/usr/local/share", "/usr/share"]);
    public IReadOnlyList<string> ConfigDirs => List("XDG_CONFIG_DIRS", ["/etc/xdg"]);

    // User directory first, then the system ones, most important first.
    public IReadOnlyList<string> DataSearchPath => [DataHome, .. DataDirs];
    public IReadOnlyList<string> ConfigSearchPath => [ConfigHome, .. ConfigDirs];

    // Folder for a user-dirs.dirs key such as "DOWNLOAD". Unknown keys fall back to the home directory.
    public string UserDir(string key)
    {
        _userDirs ??= LoadUserDirs();
        if (_userDirs.TryGetValue(key, out var path))
            return path;
        return UserDirDefaults.TryGetValue(key, out var name) ? Path.Combine(Home, name) : Home;
    }

    private Dictionary<string, string> LoadUserDirs()
    {
        var result = new Dictionary<string, string>();
        var file = Path.Combine(ConfigHome, "user-dirs.dirs");
        if (!File.Exists(file))
            return result;

        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (!line.StartsWith("XDG_", StringComparison.Ordinal))
                continue;
            int eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].EndsWith("_DIR", StringComparison.Ordinal))
                continue;

            var key = line[4..(eq - 4)];
            var value = line[(eq + 1)..].Trim().Trim('"').Replace("$HOME", Home);
            if (Path.IsPathRooted(value))
                result[key] = value;
        }
        return result;
    }

    private string? NonEmpty(string name)
    {
        var value = _env(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // The spec says relative paths in these variables must be ignored.
    private string? Absolute(string name)
    {
        var value = NonEmpty(name);
        return value is not null && Path.IsPathRooted(value) ? value : null;
    }

    private IReadOnlyList<string> List(string name, string[] fallback)
    {
        var value = NonEmpty(name);
        if (value is null)
            return fallback;
        var dirs = value.Split(':').Where(Path.IsPathRooted).ToArray();
        return dirs.Length == 0 ? fallback : dirs;
    }
}
