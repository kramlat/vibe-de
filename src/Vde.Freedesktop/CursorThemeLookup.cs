namespace Vde.Freedesktop;

// Finds Xcursor files: <icons>/<theme>/cursors/<name>. Parsing the file itself belongs to the compositor stage.
public sealed class CursorThemeLookup
{
    public const string DefaultTheme = "breeze_cursors";

    private readonly XdgDirectories _xdg;

    public CursorThemeLookup(XdgDirectories? xdg = null) => _xdg = xdg ?? XdgDirectories.Default;

    public string? Find(string cursorName, string theme = DefaultTheme)
    {
        var chain = ThemeChain.Resolve(theme, Inherits, "default");
        foreach (var t in chain)
            foreach (var root in IconRoots())
            {
                var path = Path.Combine(root, t, "cursors", cursorName);
                if (File.Exists(path))
                    return path;
            }
        return null;
    }

    private IEnumerable<string> IconRoots()
    {
        yield return Path.Combine(_xdg.Home, ".icons");
        foreach (var dir in _xdg.DataSearchPath)
            yield return Path.Combine(dir, "icons");
    }

    private IEnumerable<string> Inherits(string theme)
    {
        foreach (var root in IconRoots())
        {
            var index = Path.Combine(root, theme, "index.theme");
            if (File.Exists(index))
                return IniFile.Load(index).GetList("Icon Theme", "Inherits", ',');
        }
        return [];
    }
}
