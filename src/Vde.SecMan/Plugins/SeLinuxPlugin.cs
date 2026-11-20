using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan;

// SELinux: whether it is running and enforcing (the kernel's word, /sys/fs/selinux), and what /etc/selinux/config
// says it should be at boot.
public sealed class SeLinuxPlugin : ISecurityPlugin
{
    public string Name => "SELinux";

    public bool IsAvailable(string root)
        => File.Exists(SecurityBuilder.FilePath(root, "etc/selinux/config")) || Directory.Exists(SecurityBuilder.FilePath(root, "sys/fs/selinux"));

    public IEnumerable<string> WatchedFiles(string root) => [SecurityBuilder.FilePath(root, "etc/selinux/config")];

    public void Fill(Hive hive, string root)
    {
        var enforce = SecurityBuilder.Read(root, "sys/fs/selinux/enforce");
        var mode = enforce switch { "1" => "Enforcing", "0" => "Permissive", _ => "Disabled" };
        GeneratedHiveFill.Public(hive, Name, "Mode", RegistryValueKind.String, mode);
        GeneratedHiveFill.Public(hive, Name, "Enforcing", RegistryValueKind.Bool, mode == "Enforcing");
        if (int.TryParse(SecurityBuilder.Read(root, "sys/fs/selinux/policyvers"), out var version))
            GeneratedHiveFill.Public(hive, Name, "PolicyVersion", RegistryValueKind.Int32, version);

        var config = SecurityBuilder.Read(root, "etc/selinux/config");
        if (config is null)
            return;
        foreach (var line in config.Split('\n'))
            if (line.Split('=', 2) is [var key, var value] && !line.TrimStart().StartsWith('#'))
                switch (key.Trim())
                {
                    case "SELINUX": GeneratedHiveFill.Public(hive, Name, "ConfiguredMode", RegistryValueKind.String, char.ToUpperInvariant(value.Trim()[0]) + value.Trim()[1..].ToLowerInvariant()); break;
                    case "SELINUXTYPE": GeneratedHiveFill.Public(hive, Name, "PolicyType", RegistryValueKind.String, value.Trim()); break;
                }
    }
}
