using Vde.SysMan;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.SysMan.Tests;

// A small fake machine: what the plugins read, below a temporary root.
public sealed class FakeMachine : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sysman-" + Guid.NewGuid().ToString("N"));

    public FakeMachine()
    {
        Write("etc/hostname", "steamdeck-oled.lan\n");
        Write("etc/machine-id", "0123456789abcdef0123456789abcdef\n");
        Write("etc/timezone", "UTC\n");
        Write("etc/resolv.conf", "# generated\nsearch lan home.arpa\nnameserver 192.168.1.1\nnameserver 9.9.9.9\n");
        Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: x\n\nprocessor\t: 1\nmodel name\t: x\n\nprocessor\t: 2\n\nprocessor\t: 3\n");

        // a GPU, with a driver bound and a name from udev's hardware database
        Pci("0000:00:02.0", "0x8086", "0x1916", "0x17aa", "0x2257", "0x07", "0x030000");
        Link("sys/bus/pci/devices/0000:00:02.0/driver", "../../../../bus/pci/drivers/i915");
        Write("run/udev/data/+pci:0000:00:02.0", "S:disk/by-path/x\nI:123\nE:ID_VENDOR_FROM_DATABASE=Intel Corporation\nE:ID_MODEL_FROM_DATABASE=HD Graphics 530\nG:systemd\n");
        // a network card
        Pci("0000:03:00.0", "0x10ec", "0x8168", "0x1043", "0x8677", "0x15", "0x020000");
        // a USB keyboard and a hub
        Write("sys/bus/usb/devices/1-1/idVendor", "046d\n"); Write("sys/bus/usb/devices/1-1/idProduct", "c31c\n"); Write("sys/bus/usb/devices/1-1/bcdDevice", "6401\n");
        Write("sys/bus/usb/devices/1-1/product", "USB Keyboard\n"); Write("sys/bus/usb/devices/1-1/manufacturer", "Logitech\n"); Write("sys/bus/usb/devices/1-1/bDeviceClass", "03\n");
        Write("sys/bus/usb/devices/1-1/serial", "KB123\n");
        Write("sys/bus/usb/devices/usb1/idVendor", "1d6b\n"); Write("sys/bus/usb/devices/usb1/idProduct", "0002\n"); Write("sys/bus/usb/devices/usb1/bDeviceClass", "09\n");
        Write("sys/bus/usb/devices/1-1:1.0/bInterfaceClass", "03\n");   // an interface: no ids, not a device

        // services: one enabled, one plain, one masked, one template
        Write("usr/lib/systemd/system/sshd.service", "[Unit]\nDescription=OpenSSH Daemon\n[Service]\nUser=root\nExecStart=-/usr/bin/sshd -D\n");
        Write("usr/lib/systemd/system/cups.service", "[Unit]\nDescription=CUPS Scheduler\n[Service]\nExecStart=/usr/bin/cupsd -l\n");
        Write("usr/lib/systemd/system/getty@.service", "[Service]\nExecStart=/sbin/agetty\n");
        Write("usr/lib/systemd/system/bad.service", "[Service]\nExecStart=/bin/false\n");
        Directory.CreateDirectory(Path.Combine(Root, "etc/systemd/system/multi-user.target.wants"));
        Link("etc/systemd/system/multi-user.target.wants/sshd.service", "/usr/lib/systemd/system/sshd.service");
        Link("etc/systemd/system/bad.service", "/dev/null");
    }

    private void Pci(string slot, string vendor, string device, string subVendor, string subDevice, string revision, string cls)
    {
        var d = "sys/bus/pci/devices/" + slot + "/";
        Write(d + "vendor", vendor); Write(d + "device", device); Write(d + "subsystem_vendor", subVendor);
        Write(d + "subsystem_device", subDevice); Write(d + "revision", revision); Write(d + "class", cls);
    }

    public void Link(string path, string target)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.CreateSymbolicLink(full, target);
    }

    public void Write(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

[Collection("one at a time")]   // HiveServer.Listen sets a process-wide umask for a moment; other tests must not create folders then
public class SystemPluginTests
{
    private static Hive Build(FakeMachine m, params ISystemPlugin[] plugins)
    {
        var hive = new Hive("System") { Identity = RegistryIdentity.Root };
        SystemBuilder.Fill(hive, plugins.Length > 0 ? plugins : SystemBuilder.DefaultPlugins, m.Root);
        return hive;
    }

    [Fact]
    public void The_control_set_is_written_twice_as_ControlSet001_and_CurrentControlSet_and_Select_points_at_it()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new ControlPlugin());
        Assert.Equal(1, hive.GetValue("Select", "Current")!.Data);
        Assert.Equal(RegistryValueKind.Int32, hive.GetValue("Select", "Current")!.Kind);   // a number is Int32, not a DWORD
        foreach (var set in new[] { "ControlSet001", "CurrentControlSet" })
            Assert.Equal("STEAMDECK-OLED", hive.GetValue(set + @"\Control\ComputerName\ComputerName", "ComputerName")!.Data);   // upper case, no domain, 15 at most
    }

    [Fact]
    public void Time_zone_and_processor_facts_come_from_the_machine()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new ControlPlugin());
        var tz = @"CurrentControlSet\Control\TimeZoneInformation";
        Assert.Equal("UTC", hive.GetValue(tz, "TimeZoneKeyName")!.Data);
        Assert.Equal(0, hive.GetValue(tz, "Bias")!.Data);
        var env = @"CurrentControlSet\Control\Session Manager\Environment";
        Assert.Equal("4", hive.GetValue(env, "NUMBER_OF_PROCESSORS")!.Data);
        Assert.Equal("Windows_NT", hive.GetValue(env, "OS")!.Data);
        Assert.NotNull(hive.GetValue(env, "PROCESSOR_ARCHITECTURE"));
    }

    [Fact]
    public void PCI_devices_get_windows_hardware_ids_udev_names_the_class_and_the_bound_driver()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new ClassPlugin(), new EnumPlugin());
        var gpu = @"CurrentControlSet\Enum\PCI\VEN_8086&DEV_1916&SUBSYS_225717AA&REV_07\0000:00:02.0";

        Assert.Equal("HD Graphics 530", hive.GetValue(gpu, "DeviceDesc")!.Data);   // udev's hardware database
        Assert.Equal("Intel Corporation", hive.GetValue(gpu, "Mfg")!.Data);
        Assert.Equal("Display", hive.GetValue(gpu, "Class")!.Data);
        Assert.Equal("{4D36E968-E325-11CE-BFC1-08002BE10318}", hive.GetValue(gpu, "ClassGUID")!.Data);
        Assert.Equal("i915", hive.GetValue(gpu, "Service")!.Data);
        var ids = (string[])hive.GetValue(gpu, "HardwareID")!.Data!;
        Assert.Equal(@"PCI\VEN_8086&DEV_1916&SUBSYS_225717AA&REV_07", ids[0]);   // most specific first
        Assert.Contains(@"PCI\VEN_8086&DEV_1916&CC_030000", ids);
        Assert.Equal(@"PCI\CC_0300", ids[^1]);
        Assert.True(hive.KeyExists(@"CurrentControlSet\Control\Class\{4D36E968-E325-11CE-BFC1-08002BE10318}"));

        var nic = hive.GetSubKeyNames(@"CurrentControlSet\Enum\PCI").Single(n => n.StartsWith("VEN_10EC"));
        Assert.Equal("Net", hive.GetValue($@"CurrentControlSet\Enum\PCI\{nic}\0000:03:00.0", "Class")!.Data);
        Assert.Equal("PCI device 10ec:8168", hive.GetValue($@"CurrentControlSet\Enum\PCI\{nic}\0000:03:00.0", "DeviceDesc")!.Data);   // no udev name: a plain one
    }

    [Fact]
    public void USB_devices_use_their_serial_as_instance_skip_interfaces_and_hubs_are_USB_class()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new ClassPlugin(), new EnumPlugin());
        var kb = @"CurrentControlSet\Enum\USB\VID_046D&PID_C31C&REV_6401\KB123";
        Assert.Equal("USB Keyboard", hive.GetValue(kb, "DeviceDesc")!.Data);
        Assert.Equal("Logitech", hive.GetValue(kb, "Mfg")!.Data);
        Assert.Equal("HIDClass", hive.GetValue(kb, "Class")!.Data);
        Assert.Equal(new[] { @"USB\VID_046D&PID_C31C&REV_6401", @"USB\VID_046D&PID_C31C" }, (string[])hive.GetValue(kb, "HardwareID")!.Data!);
        Assert.Equal("USB", hive.GetValue(@"CurrentControlSet\Enum\USB\VID_1D6B&PID_0002\usb1", "Class")!.Data);
        Assert.Equal(2, hive.GetSubKeyNames(@"CurrentControlSet\Enum\USB").Count);   // the interface was not a device
    }

    [Fact]
    public void Services_come_from_unit_files_with_enabled_masked_and_template_handled()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new ServicesPlugin());
        var s = @"CurrentControlSet\Services\";
        Assert.Equal(2, hive.GetValue(s + "sshd", "Start")!.Data);                 // wanted by a target: automatic
        Assert.Equal("OpenSSH Daemon", hive.GetValue(s + "sshd", "DisplayName")!.Data);
        Assert.Equal("/usr/bin/sshd -D", hive.GetValue(s + "sshd", "ImagePath")!.Data);   // the "-" prefix is not part of the path
        Assert.Equal("root", hive.GetValue(s + "sshd", "ObjectName")!.Data);
        Assert.Equal(3, hive.GetValue(s + "cups", "Start")!.Data);                 // not enabled: manual
        Assert.Equal("LocalSystem", hive.GetValue(s + "cups", "ObjectName")!.Data);
        Assert.Equal(4, hive.GetValue(s + "bad", "Start")!.Data);                  // masked: disabled
        Assert.False(hive.KeyExists(s + "getty@"));                                // templates are not services
        Assert.Equal(16, hive.GetValue(s + "sshd", "Type")!.Data);
    }

    [Fact]
    public void Network_parameters_and_the_hardware_profile_come_from_etc()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new NetworkPlugin(), new HardwareProfilePlugin());
        var tcp = @"CurrentControlSet\Services\Tcpip\Parameters";
        Assert.Equal("steamdeck-oled", hive.GetValue(tcp, "Hostname")!.Data);
        Assert.Equal("lan", hive.GetValue(tcp, "Domain")!.Data);
        Assert.Equal("192.168.1.1,9.9.9.9", hive.GetValue(tcp, "NameServer")!.Data);
        Assert.Equal("lan,home.arpa", hive.GetValue(tcp, "SearchList")!.Data);
        Assert.Equal("{01234567-89AB-CDEF-0123-456789ABCDEF}", hive.GetValue(RegistryStore.CurrentConfigPath["System\\".Length..], "HwProfileGuid")!.Data);   // HKCC
    }

    [Fact]
    public void Anything_missing_is_skipped_and_a_failing_plugin_does_not_stop_the_rest()
    {
        using var m = new FakeMachine();
        var hive = Build(m, new FailingPlugin(), new ControlPlugin());
        Assert.Equal("boom", hive.GetValue(@"Vde\Providers\Broken", "Error")!.Data);
        Assert.Equal(1, hive.GetValue("Select", "Current")!.Data);
        var empty = new FakeMachine(); Directory.Delete(empty.Root, true); Directory.CreateDirectory(empty.Root);
        var bare = Build(empty, SystemBuilder.DefaultPlugins.ToArray());   // an empty machine: no unit files, no buses
        Assert.Equal(false, bare.GetValue(@"Vde\Providers\Services", "Available")!.Data);
        Assert.Equal(false, bare.GetValue(@"Vde\Providers\Enum", "Available")!.Data);
        empty.Dispose();
    }

    private sealed class FailingPlugin : ISystemPlugin
    {
        public string Name => "Broken";
        public bool IsAvailable(string root) => true;
        public void Fill(SystemContext context) => throw new IOException("boom");
        public IEnumerable<string> WatchedFiles(string root) => [];
        public IEnumerable<string> WatchedDirectories(string root) => [];
    }
}

[Collection("one at a time")]
public class SystemServiceTests
{
    [Fact]
    public void A_client_sees_System_as_a_key_of_HKLM_everyone_can_read_and_nobody_can_change()
    {
        using var m = new FakeMachine();
        var socket = Path.Combine(m.Root, "run", "sysman.sock");
        using var service = new SystemService(root: m.Root, identityOf: _ => new RegistryIdentity(1000, 1000, [1000]), periodic: TimeSpan.FromHours(1));
        service.Server.Listen(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        using var stop = new CancellationTokenSource();
        _ = Task.Run(() => service.Server.RunAsync(stop.Token));

        var file = new Hive("HKLM");
        file.SetValue(@"Software\Vde", "Version", RegistryValueKind.String, "1");
        using var hklm = new MountedHive(file, new Dictionary<string, IHive> { ["System"] = RemoteHive.Connect(socket) });

        Assert.Equal("1", hklm.GetValue(@"Software\Vde", "Version")!.Data);
        Assert.Equal("STEAMDECK-OLED", hklm.GetValue(@"System\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName")!.Data);
        Assert.Equal(1, hklm.GetValue(@"System\Select", "Current")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => hklm.SetValue(@"System\Select", "Current", RegistryValueKind.Int32, 2));
        Assert.Throws<UnauthorizedAccessException>(() => hklm.DeleteKey(@"System\Select"));

        m.Write("etc/hostname", "other\n");
        service.Refresh();
        Assert.Equal("OTHER", hklm.GetValue(@"System\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName")!.Data);
        stop.Cancel();
    }
}

public class RealMachineTests
{
    [Fact]
    public void The_plugins_read_this_machine_without_failing()
    {
        var hive = new Hive("System") { Identity = RegistryIdentity.Root };
        SystemBuilder.Fill(hive, SystemBuilder.DefaultPlugins, "/");

        Assert.All(hive.GetSubKeyNames(@"Vde\Providers"), p => Assert.Null(hive.GetValue(@"Vde\Providers\" + p, "Error")));
        Assert.True(hive.KeyExists(@"CurrentControlSet\Control\Class"));
        Assert.Equal(hive.GetSubKeyNames(@"ControlSet001\Services"), hive.GetSubKeyNames(@"CurrentControlSet\Services"));   // the two control sets agree
    }
}
