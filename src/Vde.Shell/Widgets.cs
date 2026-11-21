using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Vibe.Registry;

namespace Vde.Shell;

// VDE's widgets: small live panels (weather, photos from this day, the machine's load, headlines) in the spirit of the
// Windows 7 gadgets, drawn from open data and from the computer itself. Nothing here needs an account or a Microsoft service:
// the weather comes from Open-Meteo (open data, no key), headlines from RSS/Atom feeds the user chooses, photos and load from
// this machine.

public sealed record WidgetInfo(string Id, string Title, IReadOnlyList<string> IconNames);

public static class WidgetCatalog
{
    public const string Weather = "Weather", Photos = "Photos", System = "System", News = "News";

    public static readonly IReadOnlyList<WidgetInfo> All =
    [
        new(Weather, "Weather", ["weather-few-clouds", "weather-clear"]),
        new(Photos, "Photos", ["folder-pictures", "image-x-generic"]),
        new(System, "System", ["utilities-system-monitor", "computer"]),
        new(News, "Top stories", ["news-subscribe", "application-rss+xml", "internet-news-reader"]),
    ];
}

// Where the choices live: HKCU\Software\Vde\Widgets (the city, units and feeds) and ...\Widgets\Show (one switch per widget).
public sealed class WidgetSettings(IHive hive)
{
    public const string KeyPath = @"Software\Vde\Widgets";
    public const string ShowKey = KeyPath + @"\Show";

    public bool IsShown(string id) => RegistryBool.Get(hive, ShowKey, id) ?? true;
    public IReadOnlyList<WidgetInfo> Shown => WidgetCatalog.All.Where(w => IsShown(w.Id)).ToList();

    public string City => Read("WeatherCity");
    public string Units => Read("WeatherUnits") is { Length: > 0 } u ? u : "Auto";   // Auto, Celsius or Fahrenheit

    // Auto follows the region: Fahrenheit only where the metric system is not used.
    public bool Fahrenheit => Units.Equals("Fahrenheit", StringComparison.OrdinalIgnoreCase)
        || (Units.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !IsMetricRegion());

    // Feed addresses, separated by spaces or semicolons.
    public IReadOnlyList<string> Feeds => Read("Feeds").Split([' ', ';', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(f => Uri.TryCreate(f, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").ToList();

    private string Read(string name) => hive.GetValue(KeyPath, name) is { Kind: RegistryValueKind.String } v ? (string?)v.Data ?? "" : "";

    private static bool IsMetricRegion()
    {
        try { return new RegionInfo(CultureInfo.CurrentCulture.Name).IsMetric; }
        catch (ArgumentException) { return true; }
    }
}

public sealed record WeatherDay(DateOnly Date, int Code, double High, double Low);

public sealed record WeatherReport(string Place, double Temperature, string Unit, int Code, int Humidity, double Wind, IReadOnlyList<WeatherDay> Days)
{
    public string Condition => WeatherCodes.Describe(Code);
    public string IconName => WeatherCodes.Icon(Code);
}

// WMO weather codes, as Open-Meteo reports them, with the freedesktop weather icon for each.
public static class WeatherCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast", 45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle", >= 61 and <= 67 => "Rain", >= 71 and <= 77 => "Snow", >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers", >= 95 => "Thunderstorm", _ => "Unknown",
    };

    public static string Icon(int code) => code switch
    {
        0 => "weather-clear", 1 or 2 => "weather-few-clouds", 3 => "weather-overcast", 45 or 48 => "weather-fog",
        >= 51 and <= 67 => "weather-showers", >= 71 and <= 77 or 85 or 86 => "weather-snow", >= 80 and <= 82 => "weather-showers-scattered",
        >= 95 => "weather-storm", _ => "weather-severe-alert",
    };
}

public delegate Task<string> Fetch(string url, CancellationToken cancel);

public static class Http
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // The default fetcher: a plain GET that says who is asking and nothing more.
    public static async Task<string> GetAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VDE-widgets/1.0");
        using var response = await Client.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel);
    }
}

// Open-Meteo: a city name to a place (geocoding), then the current weather and the next days. Both are open data with no key.
public sealed class WeatherService(Fetch? fetch = null)
{
    private readonly Fetch _fetch = fetch ?? Http.GetAsync;

    public async Task<WeatherReport?> GetAsync(string city, bool fahrenheit, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        var place = ParsePlace(await _fetch($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=en&format=json", cancel));
        if (place is null) return null;
        var unit = fahrenheit ? "fahrenheit" : "celsius";
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={place.Value.Lat.ToString(CultureInfo.InvariantCulture)}&longitude={place.Value.Lon.ToString(CultureInfo.InvariantCulture)}"
                + $"&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5&temperature_unit={unit}";
        return ParseForecast(place.Value.Name, fahrenheit ? "°F" : "°C", await _fetch(url, cancel));
    }

    public static (double Lat, double Lon, string Name)? ParsePlace(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var first = results[0];
        var name = first.GetProperty("name").GetString() ?? "";
        if (first.TryGetProperty("admin1", out var admin) && admin.GetString() is { Length: > 0 } region) name += ", " + region;
        return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), name);
    }

    public static WeatherReport? ParseForecast(string place, string unit, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var now) || !root.TryGetProperty("daily", out var daily)) return null;
        var days = new List<WeatherDay>();
        var dates = daily.GetProperty("time"); var codes = daily.GetProperty("weather_code");
        var highs = daily.GetProperty("temperature_2m_max"); var lows = daily.GetProperty("temperature_2m_min");
        for (var i = 0; i < dates.GetArrayLength(); i++)
            days.Add(new WeatherDay(DateOnly.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture), codes[i].GetInt32(), highs[i].GetDouble(), lows[i].GetDouble()));
        return new WeatherReport(place, now.GetProperty("temperature_2m").GetDouble(), unit, now.GetProperty("weather_code").GetInt32(),
            (int)Math.Round(now.GetProperty("relative_humidity_2m").GetDouble()), now.GetProperty("wind_speed_10m").GetDouble(), days);
    }
}

// Photos that were made on this day (in an earlier year): by the files' dates, from the folders given (the Pictures folder).
public static class OnThisDay
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".gif"];

    public static IReadOnlyList<string> Find(IEnumerable<string> folders, DateOnly today, int max = 5)
    {
        var found = new List<(string Path, DateTime When)>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden };
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder, "*", options).Take(50_000))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var when = File.GetLastWriteTime(file);
                if (when.Month == today.Month && when.Day == today.Day && when.Year < today.Year) found.Add((file, when));
            }
        return found.OrderByDescending(f => f.When).Take(max).Select(f => f.Path).ToList();
    }
}

public sealed record FeedItem(string Title, string Link, string Source, DateTimeOffset? Published);

// RSS and Atom headlines.
public static class FeedReader
{
    public static IReadOnlyList<FeedItem> Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Load(XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })); }
        catch (XmlException) { return []; }
        var root = doc.Root;
        if (root is null) return [];
        var items = new List<FeedItem>();
        string Text(XElement? e) => e?.Value.Trim() ?? "";
        if (root.Name.LocalName == "rss" || root.Name.LocalName == "RDF")
        {
            var channel = root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel") ?? root;
            var source = Text(channel.Elements().FirstOrDefault(e => e.Name.LocalName == "title"));
            foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "item"))
                items.Add(new FeedItem(Text(item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")), Text(item.Elements().FirstOrDefault(e => e.Name.LocalName == "link")), source,
                    DateTimeOffset.TryParse(Text(item.Elements().FirstOrDefault(e => e.Name.LocalName == "pubDate" || e.Name.LocalName == "date")), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null));
        }
        else if (root.Name.LocalName == "feed")
        {
            var source = Text(root.Elements().FirstOrDefault(e => e.Name.LocalName == "title"));
            foreach (var entry in root.Elements().Where(e => e.Name.LocalName == "entry"))
            {
                var link = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && ((string?)e.Attribute("rel") is null or "alternate"))?.Attribute("href")?.Value ?? "";
                items.Add(new FeedItem(Text(entry.Elements().FirstOrDefault(e => e.Name.LocalName == "title")), link, source,
                    DateTimeOffset.TryParse(Text(entry.Elements().FirstOrDefault(e => e.Name.LocalName is "updated" or "published")), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null));
            }
        }
        return items.Where(i => i.Title.Length > 0).ToList();
    }

    // The newest headlines of all the feeds together; a feed that cannot be read is left out.
    public static async Task<IReadOnlyList<FeedItem>> LoadAsync(IEnumerable<string> feeds, Fetch? fetch = null, int max = 8, CancellationToken cancel = default)
    {
        fetch ??= Http.GetAsync;
        var all = new List<FeedItem>();
        foreach (var feed in feeds)
        {
            try { all.AddRange(Parse(await fetch(feed, cancel))); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { }
        }
        return all.OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue).Take(max).ToList();
    }
}
