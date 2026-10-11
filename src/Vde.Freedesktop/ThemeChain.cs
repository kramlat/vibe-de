namespace Vde.Freedesktop;

internal static class ThemeChain
{
    // The theme, then everything it inherits from (depth first, each once), then the fallback theme.
    public static List<string> Resolve(string start, Func<string, IEnumerable<string>> inherits, string? fallback)
    {
        var chain = new List<string>();
        void Visit(string name)
        {
            if (chain.Contains(name, StringComparer.Ordinal))
                return;
            chain.Add(name);
            foreach (var parent in inherits(name))
                Visit(parent);
        }
        Visit(start);
        if (fallback is not null)
            Visit(fallback);
        return chain;
    }
}
