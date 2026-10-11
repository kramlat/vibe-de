namespace Vde.Shell;

// A shell\<verb>\command line from the registry, with Unix paths: "/opt/app/run" --open "%1".
// Placeholders: %1 %L %V the first target, %2..%9 the n-th, %* all targets, %W the first target's folder.
public static class RegistryCommand
{
    public static IReadOnlyList<string> Expand(string command, IReadOnlyList<string> targets)
    {
        var args = new List<string>();
        foreach (var token in Vde.Freedesktop.ExecLine.Tokenize(command))
        {
            if (token == "%*")
            {
                args.AddRange(targets);
                continue;
            }

            var expanded = ExpandToken(token, targets);
            if (expanded.Length > 0 || !token.Contains('%'))
                args.Add(expanded);
        }
        return args;
    }

    private static string ExpandToken(string token, IReadOnlyList<string> targets)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 == token.Length)
            {
                sb.Append(token[i]);
                continue;
            }

            char c = token[++i];
            switch (c)
            {
                case '1' or 'L' or 'l' or 'V' or 'v':
                    sb.Append(Target(targets, 0));
                    break;
                case >= '2' and <= '9':
                    sb.Append(Target(targets, c - '1'));
                    break;
                case '*':
                    sb.Append(string.Join(' ', targets));
                    break;
                case 'W' or 'w':
                    sb.Append(targets.Count > 0 ? Path.GetDirectoryName(targets[0]) : string.Empty);
                    break;
                case '%':
                    sb.Append('%');
                    break;
                default:
                    sb.Append('%').Append(c);   // not ours: leave it alone
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Target(IReadOnlyList<string> targets, int index) => index < targets.Count ? targets[index] : string.Empty;
}
