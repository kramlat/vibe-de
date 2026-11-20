using System.Runtime.InteropServices;
using Vibe.Registry;

namespace Vde.SysMan;

// System\Select and the machine-wide settings of the control set: the computer's name, the time zone, the
// processor count and architecture in the environment, and whether the machine is in setup.
public sealed class ControlPlugin : ISystemPlugin
{
    public string Name => "Control";
    public bool IsAvailable(string root) => true;
    public IEnumerable<string> WatchedFiles(string root) => [Path.Combine(root, "etc/hostname"), Path.Combine(root, "etc/localtime"), Path.Combine(root, "etc/timezone")];
    public IEnumerable<string> WatchedDirectories(string root) => [];

    public void Fill(SystemContext c)
    {
        c.Key("Select");
        foreach (var name in new[] { "Current", "Default", "LastKnownGood" })
            c.Number("Select", name, 1);
        c.Number("Select", "Failed", 0);
        c.Key("Setup");
        c.Value("Setup", "SystemSetupInProgress", RegistryValueKind.Int32, 0);

        // ComputerName: the NetBIOS name, upper case and at most 15 characters, as Windows keeps it.
        var host = c.Read("etc/hostname") ?? c.Read("proc/sys/kernel/hostname") ?? "localhost";
        var netbios = new string(host.Split('.')[0].ToUpperInvariant().Take(15).ToArray());
        foreach (var key in new[] { @"Control\ComputerName\ComputerName", @"Control\ComputerName\ActiveComputerName" })
        {
            c.ControlSetKey(key);
            c.ControlSetText(key, "ComputerName", netbios);
        }

        FillTimeZone(c);

        // Session Manager\Environment: what a Windows program expects to find about the processor.
        var env = @"Control\Session Manager\Environment";
        c.ControlSetKey(env);
        c.ControlSetText(env, "OS", "Windows_NT");
        c.ControlSetText(env, "PROCESSOR_ARCHITECTURE", RuntimeInformation.OSArchitecture switch { Architecture.X64 => "AMD64", Architecture.Arm64 => "ARM64", Architecture.X86 => "x86", var a => a.ToString().ToUpperInvariant() });
        var processors = c.Read("proc/cpuinfo")?.Split('\n').Count(l => l.StartsWith("processor", StringComparison.Ordinal) && l.Contains(':'));
        c.ControlSetText(env, "NUMBER_OF_PROCESSORS", (processors is > 0 ? processors.Value : Environment.ProcessorCount).ToString());
    }

    // The zone's name from /etc/timezone or where /etc/localtime points ("../usr/share/zoneinfo/Europe/Berlin").
    private static void FillTimeZone(SystemContext c)
    {
        var name = c.Read("etc/timezone");
        if (name is null)
            try
            {
                var link = new FileInfo(c.FilePath("etc/localtime")).LinkTarget;
                if (link is not null && link.Contains("zoneinfo/", StringComparison.Ordinal))
                    name = link[(link.IndexOf("zoneinfo/", StringComparison.Ordinal) + "zoneinfo/".Length)..];
            }
            catch (IOException) { }
        if (string.IsNullOrEmpty(name))
            return;
        var key = @"Control\TimeZoneInformation";
        c.ControlSetKey(key);
        c.ControlSetText(key, "TimeZoneKeyName", name);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            c.ControlSetNumber(key, "Bias", -(int)zone.BaseUtcOffset.TotalMinutes);               // minutes west of UTC, as Windows has it
            c.ControlSetNumber(key, "ActiveTimeBias", -(int)zone.GetUtcOffset(DateTime.UtcNow).TotalMinutes);
            c.ControlSetText(key, "StandardName", zone.StandardName);
            c.ControlSetText(key, "DaylightName", zone.DaylightName);
            c.ControlSetValue(key, "DynamicDaylightTimeDisabled", RegistryValueKind.Bool, !zone.SupportsDaylightSavingTime);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
    }
}
