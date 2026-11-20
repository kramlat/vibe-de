namespace Vde.SysMan;

// Services\Tcpip\Parameters: the host name, the DNS domain, the name servers and the search list, from
// /etc/hostname and /etc/resolv.conf.
public sealed class NetworkPlugin : ISystemPlugin
{
    public string Name => "Network";
    public bool IsAvailable(string root) => File.Exists(Path.Combine(root, "etc/hostname")) || File.Exists(Path.Combine(root, "etc/resolv.conf"));
    public IEnumerable<string> WatchedFiles(string root) => [Path.Combine(root, "etc/hostname"), Path.Combine(root, "etc/resolv.conf")];
    public IEnumerable<string> WatchedDirectories(string root) => [];

    public void Fill(SystemContext c)
    {
        const string key = @"Services\Tcpip\Parameters";
        c.ControlSetKey(key);
        c.ControlSetText(key, "Hostname", (c.Read("etc/hostname") ?? "").Split('.')[0]);
        string? domain = null, search = null;
        var servers = new List<string>();
        foreach (var line in (c.Read("etc/resolv.conf") ?? "").Split('\n'))
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;
            switch (words[0])
            {
                case "nameserver": servers.Add(words[1]); break;
                case "domain": domain ??= words[1]; break;
                case "search": search ??= string.Join(',', words.Skip(1)); break;
            }
        }
        c.ControlSetText(key, "Domain", domain ?? search?.Split(',')[0]);
        c.ControlSetText(key, "NameServer", string.Join(',', servers));
        c.ControlSetText(key, "SearchList", search);
    }
}
