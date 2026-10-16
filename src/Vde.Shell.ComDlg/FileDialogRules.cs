using System.Text;

namespace Vde.Shell.ComDlg;

public enum FileDialogMode { Open, Save, Folder }

// "Text files (*.txt;*.md)" - the patterns a file name has to match to be shown, case-insensitively.
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns)
{
    public static readonly FileFilter All = new("All files", ["*"]);

    public bool Matches(string fileName)
        => Patterns.Any(p => p == "*" || p == "*.*" || Glob(p, fileName));

    // The extension a file saved under this filter gets when the user typed none: "*.txt" -> "txt".
    public string? DefaultExtension
        => Patterns.Select(p => p.StartsWith("*.", StringComparison.Ordinal) && !p.Contains('?') && p.IndexOf('*', 1) < 0 ? p[2..] : null).FirstOrDefault(e => e is { Length: > 0 });

    // "Text files|*.txt;*.md|All files|*" -> two filters. A trailing name without patterns is ignored.
    public static IReadOnlyList<FileFilter> ParseList(string spec)
    {
        var parts = spec.Split('|');
        var filters = new List<FileFilter>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts[i].Trim().Length > 0 && patterns.Length > 0)
                filters.Add(new FileFilter(parts[i].Trim(), patterns));
        }
        return filters;
    }

    private static bool Glob(string pattern, string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

public sealed class FileDialogOptions
{
    public string? Title { get; init; }
    public string? InitialFolder { get; init; }
    public string? FileName { get; init; }
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];
    public string? DefaultExtension { get; init; }   // added to a saved name that has no extension
    public bool Multiselect { get; init; }           // Open only
    public bool ConfirmOverwrite { get; init; } = true;
    public bool ShowHidden { get; init; }
}

public enum Resolve { Accept, ConfirmOverwrite, Navigate, Invalid }

public sealed record Resolution(Resolve Kind, IReadOnlyList<string> Paths, string? Message = null);

// What pressing OK means for the text in the file name box, decided without any window: whether to accept it,
// ask before overwriting, go into a folder instead, or explain what is wrong.
public static class FileDialogRules
{
    public const int MaxNameBytes = 255;

    // Names may be typed quoted and separated by spaces ("a b.txt" "c.txt") for several files.
    public static IReadOnlyList<string> SplitNames(string typed)
    {
        var names = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (var c in typed)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) names.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) names.Add(current.ToString());
        return names;
    }

    // Why a single file name cannot be used on a Unix filesystem, or null when it can.
    public static string? NameProblem(string name)
    {
        if (name.Length == 0) return "Type a file name.";
        if (name is "." or "..") return $"\"{name}\" is not a valid file name.";
        if (name.Contains('/') || name.Contains('\0')) return "A file name cannot contain \"/\".";
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes) return "That file name is too long.";
        return null;
    }

    public static Resolution ResolveOpen(string folder, string typed, FileDialogOptions options)
    {
        var names = SplitNames(typed);
        if (names.Count == 0)
            return new(Resolve.Invalid, [], "Type a file name.");
        if (names.Count > 1 && !options.Multiselect)
            return new(Resolve.Invalid, [], "Select a single file.");

        var paths = new List<string>();
        foreach (var name in names)
        {
            var path = Path.GetFullPath(name, folder);
            if (Directory.Exists(path))
                return names.Count == 1 ? new(Resolve.Navigate, [path]) : new(Resolve.Invalid, [], $"\"{name}\" is a folder.");
            if (!File.Exists(path))
                return new(Resolve.Invalid, [], $"\"{name}\" was not found.");
            paths.Add(path);
        }
        return new(Resolve.Accept, paths);
    }

    public static Resolution ResolveSave(string folder, string typed, FileFilter? filter, FileDialogOptions options)
    {
        typed = typed.Trim().Trim('"');
        if (typed.Length == 0)
            return new(Resolve.Invalid, [], "Type a file name.");

        var path = Path.GetFullPath(typed, folder);
        if (Directory.Exists(path))
            return new(Resolve.Navigate, [path]);

        var name = Path.GetFileName(path);
        if (NameProblem(name) is { } problem)
            return new(Resolve.Invalid, [], problem);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            return new(Resolve.Invalid, [], $"The folder \"{Path.GetDirectoryName(path)}\" does not exist.");

        var extension = options.DefaultExtension ?? filter?.DefaultExtension;
        if (Path.GetExtension(name).Length == 0 && extension is { Length: > 0 })
            path += "." + extension.TrimStart('.');

        if (File.Exists(path) && options.ConfirmOverwrite)
            return new(Resolve.ConfirmOverwrite, [path], $"\"{Path.GetFileName(path)}\" already exists. Do you want to replace it?");
        return new(Resolve.Accept, [path]);
    }
}
