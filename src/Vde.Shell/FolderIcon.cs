using Vde.Freedesktop;

namespace Vde.Shell;

// A folder's own icon, kept the way KDE keeps it: a ".directory" file inside the folder with
//   [Desktop Entry]
//   Icon=folder-music
// so Dolphin and the VDE file manager agree on it.
public static class FolderIcon
{
    public const string FileName = ".directory";

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
