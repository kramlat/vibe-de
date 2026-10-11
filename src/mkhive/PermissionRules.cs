using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mkhive;

// A small rules file decides who owns what and what mode it has, since a .reg file cannot say.
//
//   # comment
//   key   PATH-GLOB            MODE [UID[:GID]]
//   value PATH-GLOB NAME-GLOB  MODE [UID[:GID]]
//
// PATH-GLOB is a key path relative to the hive root, e.g. Software\Classes\*\shell\*\command
//   *  matches within one path segment, ** matches across segments, ? one character; case-insensitive.
// NAME-GLOB is a value name; @ is the default value; * matches any characters.
// MODE is octal (755). The last matching rule wins. "root" may be written for 0.
public sealed class PermissionRules
{
    public sealed record Match(ushort? Mode, uint? Uid, uint? Gid);

    private sealed record Rule(bool IsValue, Regex Path, Regex? Name, ushort Mode, uint? Uid, uint? Gid);

    private readonly List<Rule> _rules = new();

    public int Count => _rules.Count;

    public static PermissionRules Parse(string text, string source)
    {
        var rules = new PermissionRules();
        rules.Add(text, source);
        return rules;
    }

    public void Add(string text, string source)
    {
        int number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            string where = $"{source}:{number}";
            try
            {
                var tokens = Tokenize(line);
                _rules.Add(tokens[0].ToLowerInvariant() switch
                {
                    "key" when tokens.Count is 3 or 4 => new Rule(false, PathGlob(tokens[1]), null, Mode(tokens[2]), Owner(tokens, 3).uid, Owner(tokens, 3).gid),
                    "value" when tokens.Count is 4 or 5 => new Rule(true, PathGlob(tokens[1]), NameGlob(tokens[2]), Mode(tokens[3]), Owner(tokens, 4).uid, Owner(tokens, 4).gid),
                    "key" or "value" => throw new FormatException("wrong number of fields"),
                    _ => throw new FormatException($"unknown rule '{tokens[0]}' (expected key or value)"),
                });
            }
            catch (FormatException e)
            {
                throw new FormatException($"{where}: {e.Message}", e);
            }
        }
    }

    public Match ForKey(string path) => Find(isValue: false, path, null);

    public Match ForValue(string path, string name) => Find(isValue: true, path, name);

    private Match Find(bool isValue, string path, string? name)
    {
        for (int i = _rules.Count - 1; i >= 0; i--)
        {
            var rule = _rules[i];
            if (rule.IsValue == isValue && rule.Path.IsMatch(path) && (!isValue || rule.Name!.IsMatch(name!)))
                return new Match(rule.Mode, rule.Uid, rule.Gid);
        }
        return new Match(null, null, null);
    }

    // Words separated by spaces; "double quotes" keep a word together.
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else current.Append(c);
        }
        if (quoted)
            throw new FormatException("unterminated quote");
        if (any || current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static ushort Mode(string text)
    {
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
            throw new FormatException($"'{text}' is not an octal mode like 755");
        return (ushort)(Convert.ToUInt16(text, 8) & 0x1FF);
    }

    private static (uint? uid, uint? gid) Owner(List<string> tokens, int index)
    {
        if (tokens.Count <= index)
            return (null, null);
        var parts = tokens[index].Split(':');
        if (parts.Length > 2)
            throw new FormatException($"'{tokens[index]}' is not UID[:GID]");
        return (Id(parts[0]), parts.Length == 2 ? Id(parts[1]) : null);
    }

    public static uint Id(string text)
        => text == "root" ? 0
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id
            : throw new FormatException($"'{text}' is not a numeric id (or root)");

    private static Regex PathGlob(string glob)
        => Glob(glob, any: "[^\\\\]*", deep: ".*", one: "[^\\\\]");

    private static Regex? NameGlob(string glob)
        => glob == "@" ? new Regex("^$", RegexOptions.Compiled) : Glob(glob, any: ".*", deep: ".*", one: ".");

    private static Regex Glob(string glob, string any, string deep, string one)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(deep); i++; }
            else if (c == '*') sb.Append(any);
            else if (c == '?') sb.Append(one);
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
