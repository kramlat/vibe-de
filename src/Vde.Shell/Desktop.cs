using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

public readonly record struct Cell(int Column, int Row);

// Where the desktop's icons sit on a grid. Icons the user placed keep their cell; the rest fill the free cells
// down each column from the top left, in the order they are given, as on the desktops people are used to. If the screen is too small for
// them all, extra columns are added off to the right rather than stacking icons on top of each other.
public static class DesktopLayout
{
    public static IReadOnlyDictionary<string, Cell> Arrange(
        IEnumerable<string> names, IReadOnlyDictionary<string, Cell> saved, int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        var placed = new Dictionary<string, Cell>(StringComparer.Ordinal);
        var taken = new HashSet<Cell>();
        var ordered = names.ToList();   // the caller decides the order (the desktop passes folders first, then by name)

        foreach (var name in ordered)   // saved cells that are still on screen and not already used
            if (saved.TryGetValue(name, out var cell) && cell.Column >= 0 && cell.Row >= 0 && cell.Column < columns && cell.Row < rows && taken.Add(cell))
                placed[name] = cell;

        int column = 0, row = 0;
        foreach (var name in ordered.Where(n => !placed.ContainsKey(n)))
        {
            while (taken.Contains(new Cell(column, row)))
                Advance(ref column, ref row, rows);
            var cell = new Cell(column, row);
            taken.Add(cell);
            placed[name] = cell;
        }
        return placed;
    }

    private static void Advance(ref int column, ref int row, int rows)
    {
        if (++row >= rows) { row = 0; column++; }
    }

    // The cell under a point (for dropping an icon), clamped to the grid.
    public static Cell CellAt(double x, double y, double cellWidth, double cellHeight, int columns, int rows)
        => new(Math.Clamp((int)(x / cellWidth), 0, Math.Max(0, columns - 1)), Math.Clamp((int)(y / cellHeight), 0, Math.Max(0, rows - 1)));
}

public enum WallpaperMode { Fill, Fit, Stretch, Center, Tile }

public sealed record DesktopSettings(string? Wallpaper = null, WallpaperMode Mode = WallpaperMode.Fill, string BackgroundColor = "#1E1E2E", bool ShowIcons = true, int IconSize = IconSizes.Large);

// The desktop's settings and where the user put each icon, in HKCU\Software\Vde\Desktop.
public static class DesktopSettingsStore
{
    public const string KeyPath = @"Software\Vde\Desktop";
    public const string IconsKey = KeyPath + @"\Icons";

    // `fallback` is the machine-wide default (HKLM, where the installer puts the default wallpaper): it answers for every
    // value the user's hive has not set. A user who chose "None" has an empty Wallpaper value, which is not "not set".
    public static DesktopSettings Load(IHive hive, IHive? fallback = null)
    {
        static bool Has(IHive h, string name) => h.GetValue(KeyPath, name) is not null;
        IHive Source(string name) => fallback is not null && !Has(hive, name) ? fallback : hive;
        string? Text(string name) => Source(name).GetValue(KeyPath, name) is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;
        var defaults = new DesktopSettings();
        return new DesktopSettings(
            Text("Wallpaper"),
            Enum.TryParse<WallpaperMode>(Text("WallpaperMode"), true, out var mode) && Enum.IsDefined(mode) ? mode : defaults.Mode,
            Text("BackgroundColor") is { } c && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c : defaults.BackgroundColor,
            Source("ShowIcons").GetValue(KeyPath, "ShowIcons") is { Kind: RegistryValueKind.DWord, Data: uint d } ? d != 0 : defaults.ShowIcons,
            Source("IconSize").GetValue(KeyPath, "IconSize") is { Kind: RegistryValueKind.DWord, Data: uint size } && size is >= 16 and <= 256 ? IconSizes.Snap((int)size) : defaults.IconSize);
    }

    public static void Save(IHive hive, DesktopSettings settings)
    {
        // no picture is saved as an empty value, so it is not mistaken for "use the default"
        hive.SetValue(KeyPath, "Wallpaper", RegistryValueKind.String, settings.Wallpaper ?? "");
        hive.SetValue(KeyPath, "WallpaperMode", RegistryValueKind.String, settings.Mode.ToString());
        hive.SetValue(KeyPath, "BackgroundColor", RegistryValueKind.String, settings.BackgroundColor);
        hive.SetValue(KeyPath, "ShowIcons", RegistryValueKind.DWord, settings.ShowIcons ? 1u : 0u);
        hive.SetValue(KeyPath, "IconSize", RegistryValueKind.DWord, (uint)IconSizes.Snap(settings.IconSize));
    }

    public static IReadOnlyDictionary<string, Cell> LoadPositions(IHive hive)
    {
        var positions = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (!hive.KeyExists(IconsKey))
            return positions;
        foreach (var value in hive.GetValues(IconsKey))
            if (value is { Kind: RegistryValueKind.String, Data: string text } && text.Split(',') is [var c, var r]
                && int.TryParse(c, out var column) && int.TryParse(r, out var row) && column >= 0 && row >= 0)
                positions[value.Name] = new Cell(column, row);
        return positions;
    }

    public static void SetPosition(IHive hive, string fileName, Cell cell)
        => hive.SetValue(IconsKey, fileName, RegistryValueKind.String, $"{cell.Column},{cell.Row}");

    // Forgets where renamed or deleted files used to be, so the key does not grow forever.
    public static void ForgetMissing(IHive hive, IEnumerable<string> existingNames)
    {
        var keep = existingNames.ToHashSet(StringComparer.Ordinal);
        foreach (var name in LoadPositions(hive).Keys.Where(n => !keep.Contains(n)))
            hive.DeleteValue(IconsKey, name);
    }
}

public sealed record WallpaperInfo(string Name, string Path);

// The wallpapers installed on the system. There is no freedesktop standard for them, so this reads the two
// layouts in use: KDE's  wallpapers/<name>/contents/images/<width>x<height>.<ext>  and plain image files in
// backgrounds/ . For KDE packs the image closest to the screen's size is the one chosen.
public sealed class WallpaperCatalog
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".avif"];

    public static IReadOnlyList<WallpaperInfo> Find(XdgDirectories xdg, int screenWidth = 1920, int screenHeight = 1080)
    {
        var found = new Dictionary<string, WallpaperInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in xdg.DataSearchPath)
        {
            var kde = Path.Combine(dir, "wallpapers");
            if (Directory.Exists(kde))
                foreach (var pack in Directory.EnumerateDirectories(kde).OrderBy(p => p, StringComparer.Ordinal))
                    if (BestImage(Path.Combine(pack, "contents", "images"), screenWidth, screenHeight) is { } image)
                        found.TryAdd(Path.GetFileName(pack), new WallpaperInfo(Path.GetFileName(pack), image));

            var plain = Path.Combine(dir, "backgrounds");
            if (Directory.Exists(plain))
                foreach (var file in Directory.EnumerateFiles(plain, "*", SearchOption.AllDirectories).Where(IsImage).OrderBy(f => f, StringComparer.Ordinal))
                    found.TryAdd(Path.GetFileNameWithoutExtension(file), new WallpaperInfo(Path.GetFileNameWithoutExtension(file), file));
        }
        return found.Values.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // "1920x1080.jpg" beats "3840x2160.png" on a 1080p screen; with no size in any name, the first image.
    internal static string? BestImage(string folder, int width, int height)
    {
        if (!Directory.Exists(folder))
            return null;
        var images = Directory.EnumerateFiles(folder).Where(IsImage).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return images.OrderBy(i => Distance(Path.GetFileNameWithoutExtension(i), width, height)).FirstOrDefault();
    }

    private static double Distance(string name, int width, int height)
    {
        var parts = name.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
            return double.MaxValue / 2;   // no size in the name: worse than any sized one
        // never prefer a smaller image (it would be stretched); among large enough ones take the closest
        double shortfall = (w < width || h < height) ? 1_000_000 + (width - w) + (height - h) : 0;
        return shortfall + Math.Abs(w - width) + Math.Abs(h - height);
    }
}
