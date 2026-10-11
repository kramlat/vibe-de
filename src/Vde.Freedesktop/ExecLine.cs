using System.Text;

namespace Vde.Freedesktop;

// Turns a desktop entry Exec value into an argument vector, applying quoting and field codes.
public static class ExecLine
{
    // targets are absolute paths or URIs. %f/%F get local paths, %u/%U get URIs.
    public static IReadOnlyList<string> Expand(
        string exec, IReadOnlyList<string> targets, string name, string? icon, string? desktopFile)
    {
        var args = new List<string>();
        foreach (var token in Tokenize(exec))
        {
            switch (token)
            {
                case "%F":
                    args.AddRange(targets.Select(ToPath));
                    continue;
                case "%U":
                    args.AddRange(targets.Select(ToUri));
                    continue;
                case "%i":
                    if (!string.IsNullOrEmpty(icon))
                    {
                        args.Add("--icon");
                        args.Add(icon);
                    }
                    continue;
            }

            var expanded = ExpandToken(token, targets, name, desktopFile);
            if (expanded.Length > 0 || !ContainsFieldCode(token))
                args.Add(expanded);
        }
        return args;
    }

    private static string ExpandToken(string token, IReadOnlyList<string> targets, string name, string? desktopFile)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 == token.Length)
            {
                sb.Append(token[i]);
                continue;
            }
            switch (token[++i])
            {
                case '%': sb.Append('%'); break;
                case 'f' or 'F': if (targets.Count > 0) sb.Append(ToPath(targets[0])); break;
                case 'u' or 'U': if (targets.Count > 0) sb.Append(ToUri(targets[0])); break;
                case 'c': sb.Append(name); break;
                case 'k': sb.Append(desktopFile); break;
                default: break;   // %i embedded in a word, and the deprecated %d %D %n %N %v %m: dropped
            }
        }
        return sb.ToString();
    }

    private static bool ContainsFieldCode(string token)
    {
        for (int i = 0; i + 1 < token.Length; i++)
            if (token[i] == '%')
            {
                if (token[i + 1] != '%')
                    return true;
                i++;
            }
        return false;
    }

    private static string ToPath(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : target;

    private static string ToUri(string target)
        => target.StartsWith('/') ? new Uri(target).AbsoluteUri : target;

    // Splits on unquoted spaces. Inside double quotes a backslash escapes " ` $ and \.
    public static IReadOnlyList<string> Tokenize(string exec)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, started = false;

        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                    current.Append(exec[++i]);
                else if (c == '"')
                    inQuotes = false;
                else
                    current.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
                started = true;
            }
            else if (c is ' ' or '\t')
            {
                if (started || current.Length > 0)
                    tokens.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (started || current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }
}
