using System.Globalization;
using Vibe.Registry;

namespace Vde.SysMan;

// Enum\PCI and Enum\USB: the devices on the buses, from sysfs, with the names udev's hardware database gave them.
// Each is Enum\<bus>\<hardware id>\<instance>, as Windows lays device instances out: DeviceDesc, Mfg, HardwareID
// (most specific first), Class and ClassGUID, Service (the kernel driver bound to it) and where it is.
public sealed class EnumPlugin : ISystemPlugin
{
    public string Name => "Enum";

    public bool IsAvailable(string root) => Directory.Exists(Path.Combine(root, "sys/bus/pci/devices")) || Directory.Exists(Path.Combine(root, "sys/bus/usb/devices"));
    public IEnumerable<string> WatchedFiles(string root) => [];
    public IEnumerable<string> WatchedDirectories(string root) => [Path.Combine(root, "run/udev/data")];

    public void Fill(SystemContext c)
    {
        c.ControlSetKey("Enum");
        var pci = c.FilePath("sys/bus/pci/devices");
        if (Directory.Exists(pci))
            foreach (var dir in Directory.EnumerateDirectories(pci).OrderBy(d => d, StringComparer.Ordinal))
                Pci(c, dir);
        var usb = c.FilePath("sys/bus/usb/devices");
        if (Directory.Exists(usb))
            foreach (var dir in Directory.EnumerateDirectories(usb).OrderBy(d => d, StringComparer.Ordinal))
                Usb(c, dir);
    }

    private static void Pci(SystemContext c, string dir)
    {
        var slot = Path.GetFileName(dir);
        if (Hex(dir, "vendor") is not { } vendor || Hex(dir, "device") is not { } device)
            return;
        var subVendor = Hex(dir, "subsystem_vendor") ?? 0;
        var subDevice = Hex(dir, "subsystem_device") ?? 0;
        var revision = Hex(dir, "revision") ?? 0;
        var code = Hex(dir, "class") ?? 0;   // 0xCCSSPP: base class, subclass, programming interface
        var ven = $"VEN_{vendor:X4}";
        var dev = $"DEV_{device:X4}";
        var subsys = $"SUBSYS_{subDevice:X4}{subVendor:X4}";
        var rev = $"REV_{revision:X2}";
        var cc = $"CC_{code:X6}";
        var cc4 = $"CC_{code >> 8:X4}";
        var ids = new[]
        {
            $@"PCI\{ven}&{dev}&{subsys}&{rev}", $@"PCI\{ven}&{dev}&{subsys}", $@"PCI\{ven}&{dev}&{cc}", $@"PCI\{ven}&{dev}&{cc4}",
            $@"PCI\{ven}&{dev}", $@"PCI\{ven}&{cc}", $@"PCI\{ven}&{cc4}", $@"PCI\{ven}", $@"PCI\{cc}", $@"PCI\{cc4}",
        };
        var udev = c.Udev.Device("pci", slot);
        var cls = ClassPlugin.ByName((code >> 16) switch { 0x02 => "Net", 0x03 => "Display", 0x01 => "SCSIAdapter", 0x04 => "Media", 0x0C when ((code >> 8) & 0xFF) == 0x03 => "USB", _ => "System" });
        Instance(c, $@"PCI\{ven}&{dev}&{subsys}&{rev}", slot, udev?["ID_MODEL_FROM_DATABASE"] ?? $"PCI device {vendor:x4}:{device:x4}", udev?["ID_VENDOR_FROM_DATABASE"], ids, cls, Driver(dir), slot, dir, c);
    }

    private static void Usb(SystemContext c, string dir)
    {
        var name = Path.GetFileName(dir);
        if (Hex(dir, "idVendor") is not { } vendor || Hex(dir, "idProduct") is not { } product)
            return;   // an interface ("1-1:1.0") or a root hub without ids
        var rev = Hex(dir, "bcdDevice");
        var vid = $"VID_{vendor:X4}";
        var pid = $"PID_{product:X4}";
        var ids = rev is { } r
            ? new[] { $@"USB\{vid}&{pid}&REV_{r:X4}", $@"USB\{vid}&{pid}" }
            : new[] { $@"USB\{vid}&{pid}" };
        var udev = c.Udev.Device("usb", name);
        var deviceClass = Hex(dir, "bDeviceClass") ?? 0;
        var cls = ClassPlugin.ByName(deviceClass == 0x03 ? "HIDClass" : "USB");   // a hub or anything else is a USB device; a keyboard or mouse is HID
        var product_ = Text(dir, "product") ?? udev?["ID_MODEL_FROM_DATABASE"] ?? $"USB device {vendor:x4}:{product:x4}";
        var manufacturer = Text(dir, "manufacturer") ?? udev?["ID_VENDOR_FROM_DATABASE"];
        Instance(c, ids[0], Text(dir, "serial") ?? name, product_, manufacturer, ids, cls, Driver(dir), name, dir, c);
    }

    private static void Instance(SystemContext c, string hardwareId, string instance, string description, string? manufacturer, string[] ids,
        ClassPlugin.DeviceClass cls, string? service, string location, string sysfs, SystemContext context)
    {
        var key = @"Enum\" + hardwareId + "\\" + instance;
        c.ControlSetKey(key);
        c.ControlSetText(key, "DeviceDesc", description);
        c.ControlSetText(key, "Mfg", manufacturer);
        c.ControlSetValue(key, "HardwareID", RegistryValueKind.MultiString, ids);
        c.ControlSetText(key, "Class", cls.Name);
        c.ControlSetText(key, "ClassGUID", cls.Guid);
        c.ControlSetText(key, "Service", service);
        c.ControlSetText(key, "LocationInformation", location);
        c.ControlSetText(key, "SysfsPath", sysfs.StartsWith(context.Root, StringComparison.Ordinal) ? "/" + Path.GetRelativePath(context.Root, sysfs) : sysfs);
    }

    private static string? Driver(string dir)
    {
        try { return new FileInfo(Path.Combine(dir, "driver")).LinkTarget is { } target ? Path.GetFileName(target.TrimEnd('/')) : null; }
        catch (IOException) { return null; }
    }

    private static string? Text(string dir, string file)
    {
        try { return File.ReadAllText(Path.Combine(dir, file)).Trim() is { Length: > 0 } t ? t : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // sysfs writes numbers as "0x8086" or as bare hex ("1d6b").
    private static int? Hex(string dir, string file)
    {
        var text = Text(dir, file);
        if (text is null) return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
