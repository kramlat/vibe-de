using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SecMan;

// The kernel's own security switches: the active Linux security modules, kernel lockdown, Yama's ptrace scope
// and whether ordinary users may create user namespaces.
public sealed class LsmPlugin : ISecurityPlugin
{
    public string Name => "LSM";

    public bool IsAvailable(string root)
        => File.Exists(SecurityBuilder.FilePath(root, "sys/kernel/security/lsm")) || File.Exists(SecurityBuilder.FilePath(root, "proc/sys/kernel/yama/ptrace_scope"));

    public IEnumerable<string> WatchedFiles(string root) => [];

    public void Fill(Hive hive, string root)
    {
        if (SecurityBuilder.Read(root, "sys/kernel/security/lsm") is { Length: > 0 } active)
            GeneratedHiveFill.Public(hive, Name, "Active", RegistryValueKind.MultiString, active.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // "none [integrity] confidentiality": the one in brackets is the mode in force
        if (SecurityBuilder.Read(root, "sys/kernel/security/lockdown") is { } lockdown && lockdown.IndexOf('[') is var open and >= 0 && lockdown.IndexOf(']') is var close && close > open)
            GeneratedHiveFill.Public(hive, Name, "Lockdown", RegistryValueKind.String, lockdown[(open + 1)..close]);
        if (int.TryParse(SecurityBuilder.Read(root, "proc/sys/kernel/yama/ptrace_scope"), out var scope))
            GeneratedHiveFill.Public(hive, Name, "YamaPtraceScope", RegistryValueKind.Int32, scope);
        if (long.TryParse(SecurityBuilder.Read(root, "proc/sys/user/max_user_namespaces"), out var namespaces))
            GeneratedHiveFill.Public(hive, Name, "UserNamespaces", RegistryValueKind.Bool, namespaces > 0);
    }
}
