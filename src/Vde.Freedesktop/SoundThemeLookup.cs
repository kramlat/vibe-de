namespace Vde.Freedesktop;

// Sound Theme spec lookup, e.g. "dialog-error" -> /usr/share/sounds/freedesktop/stereo/dialog-error.oga
public sealed class SoundThemeLookup
{
    public const string DefaultTheme = "freedesktop";
    private static readonly string[] Extensions = [".disabled", ".oga", ".ogg", ".wav"];

    private readonly XdgDirectories _xdg;

    public SoundThemeLookup(XdgDirectories? xdg = null) => _xdg = xdg ?? XdgDirectories.Default;

    // Returns null when no theme has the sound, or when a theme marks it "<name>.disabled".
    public string? Find(string name, string theme = DefaultTheme, string profile = "stereo")
    {
        var chain = ThemeChain.Resolve(theme, Inherits, DefaultTheme);

        foreach (var t in chain)
            foreach (var root in SoundRoots())
            {
                var themeDir = Path.Combine(root, t);
                if (!Directory.Exists(themeDir))
                    continue;

                foreach (var sub in Subdirectories(themeDir, profile))
                    foreach (var ext in Extensions)
                    {
                        var path = Path.Combine(themeDir, sub, name + ext);
                        if (!File.Exists(path))
                            continue;
                        return ext == ".disabled" ? null : path;
                    }
            }
        return null;
    }

    private IEnumerable<string> SoundRoots() => _xdg.DataSearchPath.Select(d => Path.Combine(d, "sounds"));

    private IEnumerable<string> Inherits(string theme)
    {
        foreach (var root in SoundRoots())
        {
            var index = Path.Combine(root, theme, "index.theme");
            if (File.Exists(index))
                return IniFile.Load(index).GetList("Sound Theme", "Inherits", ',');
        }
        return [];
    }

    // The profile directory first (so "stereo" wins over e.g. "surround"), then the theme's own list.
    private static IEnumerable<string> Subdirectories(string themeDir, string profile)
    {
        yield return profile;
        var index = Path.Combine(themeDir, "index.theme");
        if (!File.Exists(index))
            yield break;
        foreach (var dir in IniFile.Load(index).GetList("Sound Theme", "Directories", ','))
            if (dir != profile)
                yield return dir;
    }
}
