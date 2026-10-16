using System.Text.RegularExpressions;

namespace Vde.Policy.Tests;

// VDE has no telemetry and never phones home. This enforces it: no package that exists to report, track or
// crash-upload, and no code that opens an internet connection. All IPC is local (Unix sockets, the session bus).
// If a feature genuinely needs the network later (say an opt-in update check), it must be added to the
// allowlist below in the same change, which makes it visible in review.
public class NoTelemetryTests
{
    // Packages that report data somewhere, matched against every PackageReference in every project.
    private static readonly string[] BannedPackages =
    [
        "ApplicationInsights", "Sentry", "OpenTelemetry", "Datadog", "NewRelic", "Raygun", "Bugsnag", "Rollbar",
        "AppCenter", "Segment", "Mixpanel", "Amplitude", "PostHog", "GoogleAnalytics", "Elastic.Apm", "Serilog.Sinks.Http",
        "Microsoft.Extensions.Http", "RestSharp", "Flurl",
    ];

    // APIs that open internet connections or resolve names.
    private static readonly (string Pattern, string Why)[] BannedCode =
    [
        (@"\bHttpClient\b", "HTTP client"),
        (@"\bHttpWebRequest\b|\bWebRequest\b|\bWebClient\b", "legacy web client"),
        (@"\bTcpClient\b|\bTcpListener\b|\bUdpClient\b", "TCP/UDP socket"),
        (@"\bClientWebSocket\b|\bHttpListener\b", "web socket / HTTP listener"),
        (@"AddressFamily\.InterNetwork", "internet address family"),
        (@"\bDns\.(GetHost|Resolve)", "DNS lookup"),
        (@"\bNetworkInformation\.Ping\b|\bnew Ping\(", "ping"),
        (@"System\.Net\.Http\b", "System.Net.Http"),
    ];

    // Files allowed to match a banned code pattern, with the reason. Empty on purpose.
    private static readonly Dictionary<string, string> Allowed = new();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vibe.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<string> Files(string pattern)
    {
        var root = Root();
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git"))
            .Where(f => !f.EndsWith("NoTelemetryTests.cs", StringComparison.Ordinal));   // this file names what it forbids
    }

    [Fact]
    public void No_project_references_a_telemetry_or_reporting_package()
    {
        var offenders = new List<string>();
        foreach (var project in Files("*.csproj"))
            foreach (Match m in Regex.Matches(File.ReadAllText(project), @"<PackageReference\s+Include=""([^""]+)"""))
            {
                var package = m.Groups[1].Value;
                if (BannedPackages.Any(b => package.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    offenders.Add($"{Path.GetRelativePath(Root(), project)}: {package}");
            }

        Assert.True(offenders.Count == 0, "Telemetry packages are not allowed:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_code_opens_an_internet_connection()
    {
        var offenders = new List<string>();
        foreach (var file in Files("*.cs"))
        {
            var relative = Path.GetRelativePath(Root(), file);
            if (Allowed.ContainsKey(relative))
                continue;
            var text = File.ReadAllText(file);
            foreach (var (pattern, why) in BannedCode)
                if (Regex.IsMatch(text, pattern))
                    offenders.Add($"{relative}: {why}");
        }

        Assert.True(offenders.Count == 0, "Network APIs are not allowed (VDE has no telemetry; IPC is local):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_only_sockets_are_unix_domain_ones()
    {
        // every `new Socket(` in the repo must say AddressFamily.Unix
        var offenders = new List<string>();
        foreach (var file in Files("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"new Socket\(([^)]*)\)"))
                if (!m.Groups[1].Value.Contains("AddressFamily.Unix") && !IsCheckedInheritedSocket(file, m.Groups[1].Value))
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}: new Socket({m.Groups[1].Value})");

        Assert.True(offenders.Count == 0, "Only Unix-domain sockets are allowed:\n" + string.Join("\n", offenders));
    }

    // The one socket made from a file descriptor: the one systemd passes to fhived, which fhived itself checks is a
    // Unix socket right after wrapping it.
    private static bool IsCheckedInheritedSocket(string file, string args)
        => file.EndsWith(Path.Combine("fhived", "FhivedCommand.cs"), StringComparison.Ordinal) && args.Contains("SafeSocketHandle");

    [Fact]
    public void The_guard_itself_catches_what_it_should()
    {
        Assert.Matches(BannedCode[0].Pattern, "var c = new HttpClient();");
        Assert.Matches(BannedCode[2].Pattern, "using var t = new TcpClient();");
        Assert.Contains(BannedPackages, b => "Sentry.AspNetCore".Contains(b));
        Assert.DoesNotMatch(BannedCode[0].Pattern, "var launcher = new ProcessLauncher();");
    }
}
