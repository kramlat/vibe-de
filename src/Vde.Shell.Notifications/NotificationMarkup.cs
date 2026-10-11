using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Vde.Shell.Notifications;

// Notification bodies may use a few HTML-like tags (<b>, <i>, <u>, <a href>, <img>, <br>). Applications also
// send arbitrary text, so a UI should never hand a body to a renderer as it is.
public static partial class NotificationMarkup
{
    public const int MaxSummary = 256;
    public const int MaxBody = 4096;

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    // The text without any markup: tags removed, <br> as a newline, entities decoded.
    public static string ToPlainText(string body)
    {
        var text = LineBreak().Replace(body, "\n");
        text = Tag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text);
    }

    // The links in a body, so a UI can offer them without trusting the rest of the markup. Only http, https
    // and mailto are returned: anything else (file:, javascript:, app-specific schemes) is dropped.
    public static IReadOnlyList<string> Links(string body)
    {
        var links = new List<string>();
        foreach (Match match in LinkPattern().Matches(body))
        {
            var url = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                links.Add(url);
        }
        return links;
    }

    [GeneratedRegex(@"<a\s[^>]*href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    public static string Limit(string text, int max)
    {
        if (text.Length <= max)
            return text;
        // never cut a surrogate pair in half
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return new StringBuilder(text[..cut]).Append('…').ToString();
    }
}
