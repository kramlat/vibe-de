# SYSMAN: HKEY_LOCAL_MACHINE\System

`sysman` (src/SYSMAN, logic in src/Vde.SysMan) is a small root service that shows the machine as a Windows-shaped
`System` key of HKLM, so a program (the future wineserver above all) that expects a Windows registry finds
one that looks right and is happy with it. Like PAMSAM (sam.md) and SECMAN (security.md) it only reports: the
real state stays in `/etc`, `/proc`, `/sys` and `/dev`, and nothing written here is kept.

```
systemd ─▶ sysman (root, sysman.socket) ──fhive protocol, /run/sysman/system.sock──▶ clients
                  └─ plugins read /etc, /proc, /sys and udev's database (/run/udev/data); refilled on change and every minute
clients: FhiveClient.OpenStore() mounts the socket as HKLM\System (MountedHive) when the service is running
```

udev does the heavy lifting: it already named every device (vendor and model from the hardware database, classes,
ids) and keeps the result in `/run/udev/data`; SYSMAN reads that and never probes hardware. Services come from the
service manager's own files, not from asking it. Where something else already knows (another service), the
plugin should ask it rather than reimplement it.

## Layout

The control set exists twice, as `ControlSet001` and `CurrentControlSet` (the registry has no linked keys, so both
are written together). Whole numbers are Int32 and yes/no is Bool, not Windows' DWORD.

```
Select                                      Current, Default, LastKnownGood, Failed
Setup                                       SystemSetupInProgress
CurrentControlSet\Control\ComputerName\ComputerName, ActiveComputerName   ComputerName (upper case, 15 characters)
CurrentControlSet\Control\TimeZoneInformation   TimeZoneKeyName, Bias, ActiveTimeBias, StandardName, DaylightName
CurrentControlSet\Control\Session Manager\Environment   OS, PROCESSOR_ARCHITECTURE, NUMBER_OF_PROCESSORS
CurrentControlSet\Control\Class\{guid}      the device setup classes (Net, Display, USB, HIDClass, ...)
CurrentControlSet\Enum\PCI\<ids>\<slot>     DeviceDesc, Mfg, HardwareID, Class, ClassGUID, Service (the kernel driver), SysfsPath
CurrentControlSet\Enum\USB\<ids>\<serial>   the same for USB devices
CurrentControlSet\Services\<name>           DisplayName, ImagePath, Start (2 auto, 3 manual, 4 disabled), Type, ObjectName
CurrentControlSet\Services\Tcpip\Parameters Hostname, Domain, NameServer, SearchList
CurrentControlSet\Hardware Profiles\Current and 0001   FriendlyName, HwProfileGuid (the machine id)   ← HKCC
Vde\Providers\<plugin>                      Available, Error: which plugins ran
```

## Adding a plugin

Implement `ISystemPlugin` (name, availability, `Fill` through the `SystemContext`, files and folders worth watching) and add it
to `SystemBuilder.DefaultPlugins`. Plugins read below the `root` they are given, so they are tested against a small fake
tree. Candidates: MountedDevices (from /proc/mounts and /dev/disk), network adapters, input devices (HID),
the processor and memory description (that one is HKLM\HARDWARE, a hive of its own), Control\Video.

## Notes

- The mount hides a `System` key in the system hive file, if there is one: with SYSMAN running, HKLM\System is this.
- Only PCI and USB are enumerated so far; other buses and devices (ACPI, input, block devices) are not.
- Services reads unit files only, so a unit's state is what is enabled, not what is running.
