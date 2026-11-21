using System.Diagnostics;
using System.Globalization;
using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

// One installed font family: how many faces it has (Regular, Bold, ...), the files they come from and the languages it covers.
public sealed record FontFamilyInfo(string Name, int Faces, IReadOnlyList<string> Files, IReadOnlySet<string> Languages)
{
    public string FacesText => Faces == 1 ? "1 font face" : $"{Faces} font faces";
}

// Personalization > Fonts: the installed fonts as fontconfig knows them, and adding new ones.
public static class FontCatalog
{
    public const string Format = "%{family[0]}\t%{style[0]}\t%{file}\t%{lang}\n";

    // fc-list --format output grouped by family, in name order.
    public static IReadOnlyList<FontFamilyInfo> Parse(string output)
    {
        var families = new Dictionary<string, (int Faces, HashSet<string> Files, HashSet<string> Langs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = line.Split('\t');
            if (part.Length < 3 || part[0].Length == 0) continue;
            var name = part[0].Trim();
            if (!families.TryGetValue(name, out var f)) families[name] = f = (0, new(), new(StringComparer.OrdinalIgnoreCase));
            f.Files.Add(part[2]);
            if (part.Length > 3) foreach (var tag in part[3].Split('|', StringSplitOptions.RemoveEmptyEntries)) f.Langs.Add(tag);
            families[name] = (f.Faces + 1, f.Files, f.Langs);
        }
        return families.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new FontFamilyInfo(p.Key, p.Value.Faces, p.Value.Files.ToList(), p.Value.Langs)).ToList();
    }

    // Asks fontconfig; empty when it is not installed.
    public static IReadOnlyList<FontFamilyInfo> Load()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("fc-list", ["--format", Format]) { RedirectStandardOutput = true, UseShellExecute = false });
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return Parse(output);
        }
        catch (System.ComponentModel.Win32Exception) { return []; }
    }

    // "en" -> "English"; a tag the system does not know stays as it is.
    public static string LanguageName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).EnglishName is { Length: > 0 } n && !n.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? n : tag; }
        catch (CultureNotFoundException) { return tag; }
    }
}

public sealed record FontInstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped);

public static class FontInstaller
{
    public const string SettingsKey = @"Software\Vde\Fonts";
    public const string SystemDirectory = "/usr/local/share/fonts";
    public static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];

    // "Install Locally": a font goes to this user only (on, the default) or to every user of the computer (off, asks for the password).
    public static bool InstallLocally(IHive hive) => RegistryBool.Get(hive, SettingsKey, "InstallLocally") ?? true;
    public static void SetInstallLocally(IHive hive, bool value) => RegistryBool.Set(hive, SettingsKey, "InstallLocally", value);

    public static bool IsFont(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);

    public static string LocalDirectory(XdgDirectories? xdg = null) => Path.Combine((xdg ?? XdgDirectories.Default).DataHome, "fonts");

    // Copies the font files into `directory` (made if need be); what is not a font is skipped.
    public static FontInstallResult InstallInto(IEnumerable<string> files, string directory)
    {
        var installed = new List<string>(); var skipped = new List<string>();
        foreach (var file in files)
        {
            if (!IsFont(file)) { skipped.Add(file); continue; }
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(file));
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.Ordinal))
                File.Copy(file, target, overwrite: true);
            installed.Add(target);
        }
        return new FontInstallResult(installed, skipped);
    }

    // The command that installs for all users: run through ElevationRunner, as root.
    public static IReadOnlyList<string> SystemCommand(IEnumerable<string> files, string directory = SystemDirectory)
        => ["sh", "-c", "d=\"$1\"; shift; install -d -m755 \"$d\" && install -m644 -t \"$d\" \"$@\" && fc-cache -f \"$d\"", "sh", directory, .. files];

    // Tells fontconfig about new files; harmless when it is not there.
    public static void RefreshCache(string directory)
    {
        try { using var p = Process.Start(new ProcessStartInfo("fc-cache", ["-f", directory]) { UseShellExecute = false }); p?.WaitForExit(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
