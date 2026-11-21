using Vibe.Registry;

namespace Vde.Shell;

// DOS devices: drive letters, the way Windows users know them, on a Linux system.
//   - a block device gets a letter when asked for (D:, E:, ...), remembered by the device's UUID so it keeps the letter;
//   - a folder can be mounted "in reverse" as a drive letter (like subst);
//   - a Wine prefix's drive_c is drive C: (the "main bottle").
// The map is kept in the user's registry (Software\Vde\DosDevices). File Explorer shows the letters; Sync writes the same map as the
// symlinks Wine reads (<prefix>/dosdevices/d: -> the folder, d:: -> the device), so Wine and the shell agree on what D: is.

public enum DosKind { Device, Folder, WineRoot }

// Target: Device = the device's stable id ("uuid:..."), Folder = an absolute path, WineRoot = the Wine prefix (its drive_c is the drive).
public sealed record DosMapping(char Letter, DosKind Kind, string Target, string Label = "");

public sealed record DosDrive(DosMapping Mapping, string? Path, string? DeviceNode)
{
    public char Letter => Mapping.Letter;
    public bool Available => Path is not null;
    public string Root => $"{Letter}:";
}

public static class DosLetters
{
    public const char SystemDrive = 'C';   // the main bottle's drive_c
    public const char RootDrive = 'Z';     // "/", as Wine has it

    public static bool IsLetter(char c) => char.ToUpperInvariant(c) is >= 'A' and <= 'Z';
    public static char Normalize(char c) => IsLetter(c) ? char.ToUpperInvariant(c) : throw new ArgumentException("A drive letter is A to Z.", nameof(c));

    // "d", "D:" or "d:\" to the letter; null for anything else.
    public static char? Parse(string text)
    {
        text = text.Trim();
        if (text.Length is >= 1 and <= 3 && IsLetter(text[0]) && (text.Length == 1 || (text[1] == ':' && (text.Length == 2 || text[2] is '\\' or '/'))))
            return char.ToUpperInvariant(text[0]);
        return null;
    }
}

public sealed class DosDeviceStore(IHive hive)
{
    public const string KeyPath = @"Software\Vde\DosDevices";
    private const string DrivesKey = KeyPath + @"\Drives";

    public IReadOnlyList<DosMapping> All()
    {
        if (!hive.KeyExists(DrivesKey)) return [];
        var list = new List<DosMapping>();
        foreach (var name in hive.GetSubKeyNames(DrivesKey))
            if (name.Length == 1 && DosLetters.IsLetter(name[0]) && Get(char.ToUpperInvariant(name[0])) is { } mapping)
                list.Add(mapping);
        return list.OrderBy(m => m.Letter).ToList();
    }

    public DosMapping? Get(char letter)
    {
        var key = $@"{DrivesKey}\{char.ToUpperInvariant(letter)}";
        string Read(string name) => hive.GetValue(key, name) is { Kind: RegistryValueKind.String } v ? (string?)v.Data ?? "" : "";
        return Enum.TryParse<DosKind>(Read("Kind"), out var kind) && Read("Target") is { Length: > 0 } target
            ? new DosMapping(char.ToUpperInvariant(letter), kind, target, Read("Label")) : null;
    }

    public void Set(DosMapping mapping)
    {
        var key = $@"{DrivesKey}\{DosLetters.Normalize(mapping.Letter)}";
        hive.SetValue(key, "Kind", RegistryValueKind.String, mapping.Kind.ToString());
        hive.SetValue(key, "Target", RegistryValueKind.String, mapping.Target);
        hive.SetValue(key, "Label", RegistryValueKind.String, mapping.Label);
    }

    public void Remove(char letter)
    {
        var key = $@"{DrivesKey}\{char.ToUpperInvariant(letter)}";
        if (hive.KeyExists(key)) hive.DeleteKey(key);
    }

    // The Wine prefixes kept in step with the map: the main bottle (the one whose drive_c is C:) and others the user added.
    public string? MainBottle => hive.GetValue(KeyPath, "MainBottle") is { Kind: RegistryValueKind.String, Data: string s } && s.Length > 0 ? s : null;
    public void SetMainBottle(string? prefix)
    {
        if (prefix is null) hive.DeleteValue(KeyPath, "MainBottle");
        else hive.SetValue(KeyPath, "MainBottle", RegistryValueKind.String, prefix);
    }

    public IReadOnlyList<string> Bottles => hive.GetValue(KeyPath, "Bottles") is { Kind: RegistryValueKind.MultiString } v ? [.. (string[])v.Data!] : [];
    public void SetBottles(IEnumerable<string> prefixes) => hive.SetValue(KeyPath, "Bottles", RegistryValueKind.MultiString, prefixes.Distinct().ToArray());
}

// A device's name that stays the same: its filesystem UUID (from /dev/disk/by-uuid), or its PARTUUID; /dev/sdb1 itself when it has neither.
public static class DeviceIds
{
    public static string For(string device, string disk = "/dev/disk")
    {
        foreach (var (folder, prefix) in new[] { ("by-uuid", "uuid"), ("by-partuuid", "partuuid") })
        {
            var dir = Path.Combine(disk, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var link in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    var target = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                    if (string.Equals(target, device, StringComparison.Ordinal)) return $"{prefix}:{Path.GetFileName(link)}";
                }
                catch (IOException) { }
            }
        }
        return "dev:" + device;
    }
}

public sealed class DosDevices(DosDeviceStore store, Func<IReadOnlyList<Volume>> volumes, Func<string, string>? stableId = null)
{
    private readonly Func<string, string> _stableId = stableId ?? (d => DeviceIds.For(d));

    public DosDeviceStore Store => store;

    // Where the session's own dosdevices links go: ~/.local/share/vde/dosdevices.
    public static string DefaultDirectory => Path.Combine(Vde.Freedesktop.XdgDirectories.Default.DataHome, "vde", "dosdevices");

    // Every mapped drive with where it is now: a device that is not mounted has no path.
    public IReadOnlyList<DosDrive> Drives()
    {
        var vols = volumes();
        var drives = new List<DosDrive>();
        foreach (var m in store.All())
            switch (m.Kind)
            {
                case DosKind.Device:
                    var volume = vols.FirstOrDefault(v => _stableId(v.Device) == m.Target);
                    drives.Add(new DosDrive(m, volume?.MountPoint, volume?.Device));
                    break;
                case DosKind.Folder:
                    drives.Add(new DosDrive(m, Directory.Exists(m.Target) ? m.Target : null, null));
                    break;
                default:   // WineRoot: the prefix's drive_c
                    var c = Path.Combine(m.Target, "drive_c");
                    drives.Add(new DosDrive(m, Directory.Exists(c) ? c : null, null));
                    break;
            }
        return drives;
    }

    public char? LetterOf(Volume volume) => store.All().FirstOrDefault(m => m.Kind == DosKind.Device && m.Target == _stableId(volume.Device))?.Letter;

    // The first letter from D up that is free (A and B were floppies, C is the main bottle, Z is the root).
    public char NextFree()
    {
        var used = store.All().Select(m => m.Letter).ToHashSet();
        for (var c = 'D'; c < DosLetters.RootDrive; c++)
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException("All the drive letters are in use.");
    }

    // A block device gets a letter when asked for: the one asked, or the next free. The same device asked twice keeps its letter.
    public char Assign(Volume volume, char? letter = null)
    {
        if (LetterOf(volume) is { } have && (letter is null || DosLetters.Normalize(letter.Value) == have)) return have;
        if (LetterOf(volume) is { } old) store.Remove(old);   // moving it to another letter
        var l = letter is { } asked ? Free(asked) : NextFree();
        store.Set(new DosMapping(l, DosKind.Device, _stableId(volume.Device), volume.Label));
        return l;
    }

    // A folder as a drive: the reverse of mounting a drive in a folder.
    public char MountFolder(string folder, char? letter = null, string label = "")
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"\"{folder}\" is not a folder.");
        var l = letter is { } asked ? Free(asked) : NextFree();
        store.Set(new DosMapping(l, DosKind.Folder, folder, label));
        return l;
    }

    // The main bottle: a Wine prefix whose drive_c is drive C:.
    public void SetMainBottle(string prefix)
    {
        prefix = Path.GetFullPath(prefix);
        if (!Directory.Exists(Path.Combine(prefix, "drive_c"))) throw new DirectoryNotFoundException($"\"{prefix}\" is not a Wine prefix (it has no drive_c).");
        store.Set(new DosMapping(DosLetters.SystemDrive, DosKind.WineRoot, prefix, "Wine"));
        store.SetMainBottle(prefix);
        store.SetBottles(store.Bottles.Append(prefix));
    }

    public void Unassign(char letter)
    {
        letter = DosLetters.Normalize(letter);
        if (letter == DosLetters.SystemDrive) store.SetMainBottle(null);
        store.Remove(letter);
    }

    private char Free(char letter)
    {
        letter = DosLetters.Normalize(letter);
        if (letter == DosLetters.RootDrive) throw new InvalidOperationException("Z: is the root of the file system.");
        if (letter == DosLetters.SystemDrive) throw new InvalidOperationException("C: is the main bottle's drive; use SetMainBottle.");
        if (store.Get(letter) is not null) throw new InvalidOperationException($"{letter}: is already in use.");
        return letter;
    }

    // ---- making the map real for Wine ----

    private const string Manifest = ".dosdevman";

    // Writes the map into a dosdevices folder, as Wine reads one: <letter>: a link to the folder, <letter>:: a link to the device node.
    // `prefix` is true for a Wine prefix's own dosdevices: there C: (its own drive_c) and Z: (the root) are never touched. Only links are
    // ever made, replaced or removed, and only the ones dosdevman wrote before (the manifest): a real file or folder in the way is left alone.
    public IReadOnlyList<string> Sync(string dosdevices, bool prefix = false)
    {
        var log = new List<string>();
        Directory.CreateDirectory(dosdevices);
        var manifest = Path.Combine(dosdevices, Manifest);
        var before = File.Exists(manifest) ? File.ReadAllLines(manifest).Where(l => l.Length == 1).Select(l => l[0]).ToHashSet() : [];
        var now = new HashSet<char>();

        foreach (var drive in Drives())
        {
            var l = drive.Letter;
            if (prefix && l is DosLetters.SystemDrive or DosLetters.RootDrive) continue;
            var name = char.ToLowerInvariant(l) + ":";
            if (drive.Path is null) continue;   // not there now: no link (a stale one is removed below)
            Link(Path.Combine(dosdevices, name), drive.Path, log);
            if (drive.DeviceNode is { } node) Link(Path.Combine(dosdevices, name + ":"), node, log);
            now.Add(l);
        }
        foreach (var gone in before.Except(now))
        {
            var name = char.ToLowerInvariant(gone) + ":";
            foreach (var path in new[] { Path.Combine(dosdevices, name), Path.Combine(dosdevices, name + ":") })
                if (new FileInfo(path).LinkTarget is not null) { File.Delete(path); log.Add($"removed {Path.GetFileName(path)}"); }
        }
        File.WriteAllLines(manifest, now.Order().Select(c => c.ToString()));
        return log;
    }

    private static void Link(string link, string target, List<string> log)
    {
        var info = new FileInfo(link);
        if (info.Exists || Directory.Exists(link))
        {
            if (info.LinkTarget is null) { log.Add($"left {Path.GetFileName(link)} alone, it is not a link"); return; }
            if (info.LinkTarget == target) return;
            File.Delete(link);
        }
        else if (info.LinkTarget is not null) File.Delete(link);   // a dangling link
        File.CreateSymbolicLink(link, target);
        log.Add($"{Path.GetFileName(link)} -> {target}");
    }

    // dosdevices of the VDE session and of every bottle the user keeps in step.
    public IReadOnlyList<string> SyncAll(string vdeDosdevices)
    {
        var log = new List<string>();
        log.AddRange(Sync(vdeDosdevices));
        foreach (var bottle in store.Bottles.Where(b => Directory.Exists(Path.Combine(b, "dosdevices"))))
            log.AddRange(Sync(Path.Combine(bottle, "dosdevices"), prefix: true).Select(l => $"[{bottle}] {l}"));
        return log;
    }
}
