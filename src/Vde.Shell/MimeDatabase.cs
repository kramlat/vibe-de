using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Vde.Freedesktop;

namespace Vde.Shell;

// shared-mime-info: file name -> MIME type (globs2), type hierarchy (subclasses, aliases) and descriptions.
// Content sniffing is limited to telling text from binary when the name says nothing.
public sealed class MimeDatabase
{
    public const string Directory_ = "inode/directory";
    public const string Text = "text/plain";
    public const string Binary = "application/octet-stream";

    private sealed record Glob(string Type, string Pattern, int Weight, bool CaseSensitive, Regex? Regex, int Rank);

    // Patterns are indexed so a lookup costs a few dictionary hits instead of a scan of every glob:
    // plain names, "*.ext" suffixes (keyed by lower-case ".ext"), and everything else.
    private readonly Dictionary<string, List<Glob>> _literals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Glob>> _suffixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Glob> _others = new();

    private readonly XdgDirectories _xdg;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _genericIcons = new(StringComparer.Ordinal);

    public MimeDatabase(XdgDirectories? xdg = null)
    {
        _xdg = xdg ?? XdgDirectories.Default;
        int rank = 0;
        foreach (var dir in _xdg.DataSearchPath)
        {
            var mime = Path.Combine(dir, "mime");
            LoadGlobs(Path.Combine(mime, "globs2"), rank++);
            LoadPairs(Path.Combine(mime, "aliases"), (alias, canonical) => _aliases.TryAdd(alias, canonical));
            LoadPairs(Path.Combine(mime, "subclasses"), (sub, parent) =>
            {
                if (!_parents.TryGetValue(sub, out var list))
                    _parents[sub] = list = new List<string>();
                if (!list.Contains(parent))
                    list.Add(parent);
            });
            LoadGenericIcons(Path.Combine(mime, "generic-icons"));
        }
    }

    // ---- lookup ----

    // By name only. Returns null when no pattern matches.
    // Highest weight wins, then the longest pattern. A remaining tie between different types is ambiguous
    // (the spec settles it by content, which is not implemented), so the system-wide definition wins:
    // a type from the user's own mime directory cannot take over an extension the system already knows.
    public string? GuessFromName(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        Glob? best = null;

        void Consider(Glob glob)
        {
            if (best is null || glob.Weight > best.Weight
                || (glob.Weight == best.Weight && glob.Pattern.Length > best.Pattern.Length)
                || (glob.Weight == best.Weight && glob.Pattern.Length == best.Pattern.Length && glob.Rank > best.Rank))
                best = glob;
        }

        if (_literals.TryGetValue(fileName, out var literals))
            foreach (var glob in literals.Where(g => Matches(g, fileName)))
                Consider(glob);

        for (int dot = fileName.IndexOf('.'); dot >= 0; dot = fileName.IndexOf('.', dot + 1))
            if (_suffixes.TryGetValue(fileName[dot..], out var suffixed))
                foreach (var glob in suffixed.Where(g => !g.CaseSensitive || fileName.EndsWith(g.Pattern[1..], StringComparison.Ordinal)))
                    Consider(glob);

        foreach (var glob in _others)
            if (Matches(glob, fileName))
                Consider(glob);

        return best?.Type;
    }

    private void Index(Glob glob)
    {
        var p = glob.Pattern;
        bool wildcard = p.IndexOfAny(['*', '?', '[']) >= 0;
        if (!wildcard)
            Add(_literals, p, glob);
        else if (p.Length > 2 && p[0] == '*' && p[1] == '.' && p.IndexOfAny(['*', '?', '['], 1) < 0)
            Add(_suffixes, p[1..], glob);
        else
            _others.Add(glob);
    }

    private static void Add(Dictionary<string, List<Glob>> map, string key, Glob glob)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<Glob>();
        list.Add(glob);
    }

    // Directories, then the name, then a peek at the content to separate text from binary.
    public string GuessForFile(string path)
    {
        if (System.IO.Directory.Exists(path))
            return Directory_;

        var byName = GuessFromName(path);
        if (byName is not null || !File.Exists(path))
            return byName ?? Binary;

        return LooksLikeText(path) ? Text : Binary;
    }

    public string Canonical(string mimeType) => _aliases.GetValueOrDefault(mimeType, mimeType);

    // The type itself first, then its ancestors: text/x-csrc -> text/plain -> application/octet-stream.
    public IReadOnlyList<string> WithAncestors(string mimeType)
    {
        var result = new List<string>();
        void Visit(string type)
        {
            type = Canonical(type);
            if (result.Contains(type))
                return;
            result.Add(type);
            if (_parents.TryGetValue(type, out var parents))
                foreach (var parent in parents)
                    Visit(parent);
        }
        Visit(mimeType);

        // Every text/* type is a text/plain, and everything is an octet-stream, even when not listed.
        if (result[0].StartsWith("text/", StringComparison.Ordinal) && !result.Contains(Text))
            result.Add(Text);
        if (!result.Contains(Binary))
            result.Add(Binary);
        return result;
    }

    // Icon names to try, most specific first: "image-jpeg", then the generic "image-x-generic".
    public IReadOnlyList<string> IconNames(string mimeType)
    {
        mimeType = Canonical(mimeType);
        var names = new List<string> { mimeType.Replace('/', '-') };
        if (_genericIcons.TryGetValue(mimeType, out var generic))
            names.Add(generic);
        else
            names.Add(mimeType[..mimeType.IndexOf('/')] + "-x-generic");
        return names;
    }

    // "JPEG image", localized if the database has the language, else English. Falls back to the type name.
    public string Describe(string mimeType, IReadOnlyList<string> locales)
    {
        mimeType = Canonical(mimeType);
        foreach (var dir in _xdg.DataSearchPath)
        {
            var file = Path.Combine(dir, "mime", mimeType + ".xml");
            if (!File.Exists(file))
                continue;

            string? english = null;
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var found = new Dictionary<string, string>();
            bool more = reader.Read();
            while (more)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "comment")
                {
                    var lang = reader.GetAttribute("xml:lang");
                    var text = reader.ReadElementContentAsString();   // leaves the reader on the next node
                    if (lang is null)
                        english = text;
                    else
                        found[lang.Replace('-', '_')] = text;
                    continue;
                }
                more = reader.Read();
            }

            foreach (var locale in locales)
                if (found.TryGetValue(locale, out var localized))
                    return localized;
            return english ?? mimeType;
        }
        return mimeType;
    }

    // ---- loading ----

    private void LoadGlobs(string file, int rank)
    {
        if (!File.Exists(file))
            return;

        foreach (var raw in File.ReadLines(file))
        {
            if (raw.Length == 0 || raw[0] == '#')
                continue;
            // weight:type:pattern[:flags]
            var parts = raw.Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[0], out var weight))
                continue;

            var pattern = parts[2];
            bool caseSensitive = parts.Length > 3 && parts[3].Split(',').Contains("cs");
            bool hasWildcard = pattern.IndexOfAny(['*', '?', '[']) >= 0;
            var regex = hasWildcard ? new Regex(GlobToRegex(pattern), RegexOptions.Compiled | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)) : null;
            Index(new Glob(parts[1], pattern, weight, caseSensitive, regex, rank));
        }
    }

    private static bool Matches(Glob glob, string fileName)
        => glob.Regex is not null
            ? glob.Regex.IsMatch(fileName)
            : string.Equals(glob.Pattern, fileName, glob.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        foreach (var c in glob)
            sb.Append(c switch { '*' => ".*", '?' => ".", '[' or ']' => c.ToString(), _ => Regex.Escape(c.ToString()) });
        return sb.Append('$').ToString();
    }

    private static void LoadPairs(string file, Action<string, string> add)
    {
        if (!File.Exists(file))
            return;
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0][0] != '#')
                add(parts[0], parts[1]);
        }
    }

    private void LoadGenericIcons(string file)
    {
        if (!File.Exists(file))
            return;
        foreach (var line in File.ReadLines(file))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[0] != '#')
                _genericIcons.TryAdd(line[..colon], line[(colon + 1)..]);
        }
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[512];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return true;   // an empty file is plain text
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                return false;
            try
            {
                // A multi-byte character cut off at the 512-byte edge is fine; invalid bytes elsewhere are not.
                new UTF8Encoding(false, true).GetCharCount(buffer, 0, read - Math.Min(3, read));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
