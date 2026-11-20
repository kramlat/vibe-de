using Vibe.Registry;

namespace Vde.SysMan;

// Hardware Profiles\0001 and Current: the one hardware profile, identified by the machine id. HKCC is this key.
public sealed class HardwareProfilePlugin : ISystemPlugin
{
    public string Name => "HardwareProfile";
    public bool IsAvailable(string root) => true;
    public IEnumerable<string> WatchedFiles(string root) => [];
    public IEnumerable<string> WatchedDirectories(string root) => [];

    public void Fill(SystemContext c)
    {
        // /etc/machine-id is 32 hex digits: the same number written as a GUID
        var id = c.Read("etc/machine-id");
        var guid = id is { Length: 32 } && Guid.TryParseExact(id, "N", out var g) ? g : Guid.Empty;
        foreach (var profile in new[] { @"Hardware Profiles\0001", @"Hardware Profiles\Current" })
        {
            c.ControlSetKey(profile);
            c.ControlSetText(profile, "FriendlyName", "Profile 1");
            c.ControlSetText(profile, "HwProfileGuid", guid.ToString("B").ToUpperInvariant());
            c.ControlSetNumber(profile, "PreferenceOrder", 0);
            c.ControlSetValue(profile, "Aliasable", RegistryValueKind.Bool, true);
        }
    }
}
