using System.Text;

namespace Vde.Freedesktop;

// The key-file syntax shared by .desktop files, index.theme and mimeapps.list.
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _groups = new(StringComparer.Ordinal);

    public IEnumerable<string> GroupNames => _groups.Keys;

    public static IniFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static IniFile Parse(string text)
    {
        var file = new IniFile();
        Dictionary<string, string>? group = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1];
                // A repeated group merges, but a repeated key keeps its first value.
                if (!file._groups.TryGetValue(name, out group))
                    file._groups[name] = group = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            int eq = line.IndexOf('=');
            if (group is null || eq <= 0)
                continue;   // junk before the first group or without a key: ignore, as desktop environments do
            group.TryAdd(line[..eq].TrimEnd(), line[(eq + 1)..].TrimStart());
        }
        return file;
    }

    public bool HasGroup(string group) => _groups.ContainsKey(group);

    public string? GetRaw(string group, string key)
        => _groups.TryGetValue(group, out var g) && g.TryGetValue(key, out var v) ? v : null;

    public string? Get(string group, string key)
        => GetRaw(group, key) is { } raw ? Unescape(raw) : null;

    // Looks up Key[locale] for each candidate in order, then plain Key. See Locale.Candidates.
    public string? GetLocalized(string group, string key, IReadOnlyList<string> localeCandidates)
    {
        foreach (var locale in localeCandidates)
            if (Get(group, $"{key}[{locale}]") is { } localized)
                return localized;
        return Get(group, key);
    }

    public bool GetBool(string group, string key, bool fallback = false)
        => GetRaw(group, key) switch { "true" => true, "false" => false, _ => fallback };

    // A ';' separated list. "\;" keeps a semicolon inside an item.
    public IReadOnlyList<string> GetList(string group, string key, char separator = ';')
    {
        var raw = GetRaw(group, key);
        if (string.IsNullOrEmpty(raw))
            return [];

        var items = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length && raw[i + 1] == separator)
            {
                current.Append(separator);
                i++;
            }
            else if (raw[i] == separator)
            {
                items.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(raw[i]);
            }
        }
        if (current.Length > 0)
            items.Add(Unescape(current.ToString()));
        return items;
    }

    public static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            char n = value[++i];
            switch (n)
            {
                case 's': sb.Append(' '); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                case ';': sb.Append(';'); break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }
}
