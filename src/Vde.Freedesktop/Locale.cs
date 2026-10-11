namespace Vde.Freedesktop;

public static class Locale
{
    // "de_AT.UTF-8@euro" -> de_AT@euro, de_AT, de@euro, de  (most specific first).
    public static IReadOnlyList<string> Candidates(string? locale)
    {
        if (string.IsNullOrEmpty(locale) || locale is "C" or "POSIX")
            return [];

        string modifier = "", country = "";
        int at = locale.IndexOf('@');
        if (at >= 0) { modifier = locale[(at + 1)..]; locale = locale[..at]; }
        int dot = locale.IndexOf('.');
        if (dot >= 0) locale = locale[..dot];
        int us = locale.IndexOf('_');
        if (us >= 0) { country = locale[(us + 1)..]; locale = locale[..us]; }

        var result = new List<string>();
        if (country.Length > 0 && modifier.Length > 0) result.Add($"{locale}_{country}@{modifier}");
        if (country.Length > 0) result.Add($"{locale}_{country}");
        if (modifier.Length > 0) result.Add($"{locale}@{modifier}");
        result.Add(locale);
        return result;
    }

    public static IReadOnlyList<string> FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            if (env(name) is { Length: > 0 } value)
                return Candidates(value);
        return [];
    }
}
