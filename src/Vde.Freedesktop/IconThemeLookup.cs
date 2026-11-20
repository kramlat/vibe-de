namespace Vde.Freedesktop;

public sealed record IconFile(string Path)
{
    public bool IsScalable => Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
}

// Icon Theme spec lookup: theme inheritance, size matching, hicolor fallback and name shortening.
public sealed class IconThemeLookup
{
    public const string DefaultTheme = "breeze";
    private static readonly string[] Extensions = [".png", ".svg", ".xpm"];

    private enum DirType { Fixed, Scalable, Threshold }

    private sealed record IconDir(string Path, int Size, int Scale, DirType Type, int MinSize, int MaxSize, int Threshold)
    {
        public int Distance(int size, int scale)
        {
            int wanted = size * scale;
            switch (Type)
            {
                case DirType.Fixed:
                    return Math.Abs(Size * Scale - wanted);
                case DirType.Scalable:
                    if (wanted < MinSize * Scale) return MinSize * Scale - wanted;
                    return wanted > MaxSize * Scale ? wanted - MaxSize * Scale : 0;
                default:
                    if (wanted < (Size - Threshold) * Scale) return (Size - Threshold) * Scale - wanted;
                    return wanted > (Size + Threshold) * Scale ? wanted - (Size + Threshold) * Scale : 0;
            }
        }
    }

    private sealed record Theme(IReadOnlyList<string> Inherits, IReadOnlyList<string> Roots, IReadOnlyList<IconDir> Dirs);

    private readonly XdgDirectories _xdg;
    private readonly Dictionary<string, Theme?> _themes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _listings = new(StringComparer.Ordinal);

    public IconThemeLookup(XdgDirectories? xdg = null) => _xdg = xdg ?? XdgDirectories.Default;

    // size is in logical pixels; scale is the output scale factor (2 on HiDPI).
    public IconFile? Find(string name, int size, int scale = 1, string theme = DefaultTheme)
    {
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? new IconFile(name) : null;

        var chain = ThemeChain.Resolve(theme, t => Load(t)?.Inherits ?? [], "hicolor");

        // "network-wireless-signal-good" falls back to "network-wireless-signal", then "network-wireless", ...
        for (var candidate = name; ; )
        {
            foreach (var t in chain)
                if (Lookup(t, candidate, size, scale) is { } found)
                    return found;

            int dash = candidate.LastIndexOf('-');
            if (dash <= 0)
                break;
            candidate = candidate[..dash];
        }
        return LookupPixmaps(name);
    }

    private IconFile? Lookup(string themeName, string icon, int size, int scale)
    {
        var theme = Load(themeName);
        if (theme is null)
            return null;

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (var dir in theme.Dirs)
        {
            int distance = dir.Distance(size, scale);
            if (distance >= bestDistance)
                continue;

            foreach (var root in theme.Roots)
            {
                var folder = System.IO.Path.Combine(root, dir.Path);
                var names = Listing(folder);
                foreach (var ext in Extensions)
                    if (names.Contains(icon + ext))
                    {
                        best = System.IO.Path.Combine(folder, icon + ext);
                        bestDistance = distance;
                        goto nextDir;
                    }
            }
            nextDir:
            if (bestDistance == 0)
                break;
        }
        return best is null ? null : new IconFile(best);
    }

    private IconFile? LookupPixmaps(string name)
    {
        foreach (var dataDir in _xdg.DataDirs)
        {
            var names = Listing(System.IO.Path.Combine(dataDir, "pixmaps"));
            foreach (var ext in Extensions)
                if (names.Contains(name + ext))
                    return new IconFile(System.IO.Path.Combine(dataDir, "pixmaps", name + ext));
        }
        return null;
    }

    // A short text that changes when the installed icon themes do (a theme directory or its index.theme was
    // touched): what a cache of rendered icons uses to know its pictures may be out of date. Cheap: a few stats.
    public string Stamp(string theme = DefaultTheme)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { theme, "hicolor" })
            foreach (var root in IconRoots().Select(r => System.IO.Path.Combine(r, name)))
            {
                if (!Directory.Exists(root)) continue;
                sb.Append(root).Append('=').Append(Directory.GetLastWriteTimeUtc(root).Ticks);
                var index = System.IO.Path.Combine(root, "index.theme");
                if (File.Exists(index)) sb.Append('/').Append(File.GetLastWriteTimeUtc(index).Ticks);
                sb.Append(';');
            }
        return sb.ToString();
    }

    private IEnumerable<string> IconRoots()
    {
        yield return System.IO.Path.Combine(_xdg.Home, ".icons");
        foreach (var dir in _xdg.DataSearchPath)
            yield return System.IO.Path.Combine(dir, "icons");
    }

    private Theme? Load(string name)
    {
        if (_themes.TryGetValue(name, out var cached))
            return cached;

        var roots = IconRoots().Select(r => System.IO.Path.Combine(r, name)).Where(Directory.Exists).ToList();
        IniFile? index = null;
        foreach (var root in roots)
        {
            var file = System.IO.Path.Combine(root, "index.theme");
            if (File.Exists(file))
            {
                index = IniFile.Load(file);
                break;
            }
        }

        Theme? theme = null;
        if (index is not null && index.HasGroup("Icon Theme"))
        {
            var dirs = new List<IconDir>();
            foreach (var key in new[] { "Directories", "ScaledDirectories" })
                foreach (var dirName in index.GetList("Icon Theme", key, ','))
                    if (ReadDir(index, dirName) is { } dir)
                        dirs.Add(dir);

            theme = new Theme(index.GetList("Icon Theme", "Inherits", ','), roots, dirs);
        }
        _themes[name] = theme;
        return theme;
    }

    private static IconDir? ReadDir(IniFile index, string dirName)
    {
        if (!int.TryParse(index.Get(dirName, "Size"), out var size))
            return null;   // directory listed but never described

        var type = index.Get(dirName, "Type") switch
        {
            "Fixed" => DirType.Fixed,
            "Scalable" => DirType.Scalable,
            _ => DirType.Threshold,
        };
        return new IconDir(
            dirName, size,
            int.TryParse(index.Get(dirName, "Scale"), out var scale) ? scale : 1,
            type,
            int.TryParse(index.Get(dirName, "MinSize"), out var min) ? min : size,
            int.TryParse(index.Get(dirName, "MaxSize"), out var max) ? max : size,
            int.TryParse(index.Get(dirName, "Threshold"), out var threshold) ? threshold : 2);
    }

    private HashSet<string> Listing(string folder)
    {
        if (!_listings.TryGetValue(folder, out var names))
        {
            names = Directory.Exists(folder)
                ? new HashSet<string>(Directory.EnumerateFiles(folder).Select(System.IO.Path.GetFileName)!, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            _listings[folder] = names;
        }
        return names;
    }
}
