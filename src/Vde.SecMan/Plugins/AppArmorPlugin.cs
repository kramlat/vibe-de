using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan;

// AppArmor: whether the module is on, how many profiles are loaded and in which mode. The list of profiles
// (the kernel only tells root) is root's to read; the counts are public.
public sealed class AppArmorPlugin : ISecurityPlugin
{
    public string Name => "AppArmor";
    public bool IsAvailable(string root) => Directory.Exists(SecurityBuilder.FilePath(root, "sys/module/apparmor"));
    public IEnumerable<string> WatchedFiles(string root) => [];

    public void Fill(Hive hive, string root)
    {
        GeneratedHiveFill.Public(hive, Name, "Enabled", RegistryValueKind.Bool, SecurityBuilder.Read(root, "sys/module/apparmor/parameters/enabled") == "Y");
        var text = SecurityBuilder.Read(root, "sys/kernel/security/apparmor/profiles");
        if (text is null)
            return;   // not root, or the securityfs is not mounted: no profile list
        var profiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.LastIndexOf(" (", StringComparison.Ordinal) is var at and > 0 && line.EndsWith(')'))
                profiles[line[..at]] = line[(at + 2)..^1];   // "/usr/bin/man (enforce)"
        GeneratedHiveFill.Public(hive, Name, "ProfileCount", RegistryValueKind.Int32, profiles.Count);
        GeneratedHiveFill.Public(hive, Name, "EnforcedCount", RegistryValueKind.Int32, profiles.Count(p => p.Value == "enforce"));
        GeneratedHiveFill.Public(hive, Name, "ComplainingCount", RegistryValueKind.Int32, profiles.Count(p => p.Value == "complain"));
        GeneratedHiveFill.Key(hive, Name + @"\Profiles");
        foreach (var (profile, mode) in profiles)
        {
            var key = Name + @"\Profiles\" + profile.Replace('\\', '/');
            GeneratedHiveFill.Key(hive, key);
            GeneratedHiveFill.Restricted(hive, key, "Mode", RegistryValueKind.String, mode);
        }
    }
}
