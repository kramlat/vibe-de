namespace Vde.SysMan;

// Control\Class\{GUID}: the device setup classes, with the well-known class GUIDs Windows (and a program asking
// about devices) uses, and which Linux subsystem or class feeds each. EnumPlugin refers to these.
public sealed class ClassPlugin : ISystemPlugin
{
    public sealed record DeviceClass(string Name, string Guid, string Description);

    public static IReadOnlyList<DeviceClass> Classes { get; } =
    [
        new("System", "{4D36E97D-E325-11CE-BFC1-08002BE10318}", "System devices"),
        new("Net", "{4D36E972-E325-11CE-BFC1-08002BE10318}", "Network adapters"),
        new("Display", "{4D36E968-E325-11CE-BFC1-08002BE10318}", "Display adapters"),
        new("DiskDrive", "{4D36E967-E325-11CE-BFC1-08002BE10318}", "Disk drives"),
        new("SCSIAdapter", "{4D36E97B-E325-11CE-BFC1-08002BE10318}", "Storage controllers"),
        new("Keyboard", "{4D36E96B-E325-11CE-BFC1-08002BE10318}", "Keyboards"),
        new("Mouse", "{4D36E96F-E325-11CE-BFC1-08002BE10318}", "Mice and other pointing devices"),
        new("Media", "{4D36E96C-E325-11CE-BFC1-08002BE10318}", "Sound, video and game controllers"),
        new("USB", "{36FC9E60-C465-11CF-8056-444553540000}", "Universal Serial Bus controllers"),
        new("HIDClass", "{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}", "Human Interface Devices"),
        new("Bluetooth", "{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}", "Bluetooth"),
        new("Image", "{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}", "Imaging devices"),
        new("Processor", "{50127DC3-0F36-415E-A6CC-4CB3BE910B65}", "Processors"),
    ];

    public static DeviceClass ByName(string name) => Classes.First(c => c.Name == name);

    public string Name => "Class";
    public bool IsAvailable(string root) => true;
    public IEnumerable<string> WatchedFiles(string root) => [];
    public IEnumerable<string> WatchedDirectories(string root) => [];

    public void Fill(SystemContext c)
    {
        foreach (var cls in Classes)
        {
            var key = @"Control\Class\" + cls.Guid;
            c.ControlSetKey(key);
            c.ControlSetText(key, "Class", cls.Name);
            c.ControlSetText(key, "", cls.Description);
        }
    }
}
