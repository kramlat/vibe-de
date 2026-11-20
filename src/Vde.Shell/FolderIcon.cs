using Vde.Freedesktop;

namespace Vde.Shell;

// A folder's own icon, kept the way KDE keeps it: a ".directory" file inside the folder with
//   [Desktop Entry]
//   Icon=folder-music
// so Dolphin and the VDE file manager agree on it.
public static class FolderIcon
{
    public const string FileName = ".directory";

    // The icon a folder shows: its own (.directory) when it has one, then the one its role has (the home folder,
    // Documents, Downloads...), then the plain folder. The way Dolphin and Plasma choose.
    public static IReadOnlyList<string> NamesFor(string folder, KnownFolders? known = null)
    {
        var names = new List<string>();
        if (Read(folder) is { } custom)
            names.Add(custom);
        if (known is not null)
        {
            var full = Path.GetFullPath(folder).TrimEnd('/');
            foreach (var (which, icon) in Roles)
                if (string.Equals(known.Get(which).TrimEnd('/'), full, StringComparison.Ordinal))
                {
                    names.Add(icon);
                    break;
                }
        }
        if (Path.GetFullPath(folder) == "/")
            names.Add("drive-harddisk");
        names.Add("folder");
        return names;
    }

    private static readonly (KnownFolder Which, string Icon)[] Roles =
    [
        (KnownFolder.Home, "user-home"), (KnownFolder.Desktop, "user-desktop"), (KnownFolder.Documents, "folder-documents"),
        (KnownFolder.Downloads, "folder-download"), (KnownFolder.Music, "folder-music"), (KnownFolder.Pictures, "folder-pictures"),
        (KnownFolder.Videos, "folder-videos"), (KnownFolder.Templates, "folder-templates"), (KnownFolder.PublicShare, "folder-publicshare"),
    ];

    // null when the folder has no custom icon (or the file is unreadable or says something odd).
    public static string? Read(string folder)
    {
        var file = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(file)) return null;
            return IsSafeName(IniFile.Load(file).Get("Desktop Entry", "Icon"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // null clears the custom icon. The rest of an existing .directory file is left alone.
    public static void Write(string folder, string? icon)
    {
        var file = Path.Combine(folder, FileName);
        if (icon is null)
        {
            if (!File.Exists(file)) return;
            var ini = IniFile.Load(file);
            if (ini.GetRaw("Desktop Entry", "Icon") is null) return;
            // drop just the Icon line: write the file back without it
            File.WriteAllLines(file, File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("Icon=", StringComparison.Ordinal)));
            return;
        }
        if (IsSafeName(icon) is null)
            throw new ArgumentException("An icon name is letters, digits and - _ . only (or an absolute path to an image).", nameof(icon));
        IniEditor.SetValue(file, "Desktop Entry", "Icon", icon);
    }

    // Icon names are freedesktop names ("folder-music") or an absolute image path. Anything with control
    // characters or newlines would let one setting write another line into the file.
    private static string? IsSafeName(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon) || icon.Length > 512 || icon.Any(char.IsControl)) return null;
        if (icon.StartsWith('/')) return icon;
        return icon.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? icon : null;
    }
}
