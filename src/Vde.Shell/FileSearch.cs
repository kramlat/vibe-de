using System.Text;

namespace Vde.Shell;

public sealed record FileHit(string Path, string Name, bool IsDirectory, int? Line = null, string? Snippet = null);

public sealed class FileSearchOptions
{
    public int MaxResults { get; init; } = 50;
    public int MaxContentBytes { get; init; } = 2 * 1024 * 1024;   // larger files are not read for content
    public bool IncludeHidden { get; init; }
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(3);   // stop looking after this long and return what was found
}

// Local file search: by name ("find . -name '*x*'") and by content ("grep -rn x ."). Reads nothing outside the
// folders it is given, never follows symlinks (no loops, no escaping the roots), and always stops: by result
// count, by time budget or when cancelled.
public static class FileSearch
{
    // Names containing the text (case-insensitive), or matching a glob when it has * or ?. Name matches first.
    public static IReadOnlyList<FileHit> ByName(IEnumerable<string> roots, string pattern, FileSearchOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new();
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return [];
        var matches = pattern.IndexOfAny(['*', '?']) >= 0 ? GlobMatcher(pattern) : (name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

        var hits = new List<FileHit>();
        Walk(roots, options, cancel, (path, isDirectory) =>
        {
            var name = System.IO.Path.GetFileName(path);
            if (matches(name))
                hits.Add(new FileHit(path, name, isDirectory));
            return hits.Count < options.MaxResults;
        });
        // names that start with the text come first, then shorter names, then the walk order
        return hits.OrderBy(h => h.Name.StartsWith(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(h => h.Name.Length).ToList();
    }

    // Lines containing the text, in text files only (a NUL byte in the first block means binary: skipped).
    public static IReadOnlyList<FileHit> ByContent(IEnumerable<string> roots, string text, FileSearchOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new();
        if (text.Length == 0)
            return [];

        var hits = new List<FileHit>();
        Walk(roots, options, cancel, (path, isDirectory) =>
        {
            if (!isDirectory)
                SearchFile(path, text, options, hits, cancel);
            return hits.Count < options.MaxResults;
        });
        return hits;
    }

    private static void SearchFile(string path, string text, FileSearchOptions options, List<FileHit> hits, CancellationToken cancel)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > options.MaxContentBytes)
                return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[Math.Min(4096, (int)info.Length)];
            int read = stream.Read(head, 0, head.Length);
            if (Array.IndexOf(head, (byte)0, 0, read) >= 0)
                return;   // binary
            stream.Position = 0;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
            int number = 0;
            while (reader.ReadLine() is { } line)
            {
                cancel.ThrowIfCancellationRequested();
                number++;
                if (line.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new FileHit(path, System.IO.Path.GetFileName(path), false, number, Snippet(line, text)));
                    if (hits.Count >= options.MaxResults)
                        return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Snippet(string line, string text)
    {
        line = line.Trim();
        if (line.Length <= 160)
            return line;
        int at = Math.Max(0, line.IndexOf(text, StringComparison.OrdinalIgnoreCase) - 60);
        return (at > 0 ? "…" : "") + line.Substring(at, Math.Min(160, line.Length - at)) + (at + 160 < line.Length ? "…" : "");
    }

    // Depth-first over the roots. visit returns false to stop. Cancellation throws; the time budget just ends the walk.
    private static void Walk(IEnumerable<string> roots, FileSearchOptions options, CancellationToken cancel, Func<string, bool, bool> visit)
    {
        var deadline = DateTime.UtcNow + options.Budget;
        var stack = new Stack<string>(roots.Where(Directory.Exists).Reverse());
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                return;
            var folder = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            var subfolders = new List<string>();
            foreach (var child in children)
            {
                if (child.Name.StartsWith('.') && !options.IncludeHidden)
                    continue;
                if (child.LinkTarget is not null)
                    continue;   // links are neither followed nor listed: no loops, no leaving the roots
                bool isDirectory = child is DirectoryInfo;
                if (!visit(child.FullName, isDirectory))
                    return;
                if (isDirectory)
                    subfolders.Add(child.FullName);
            }
            for (int i = subfolders.Count - 1; i >= 0; i--)
                stack.Push(subfolders[i]);
        }
    }

    private static Func<string, bool> GlobMatcher(string glob)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }
}
