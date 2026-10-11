using System.Text;
using System.Text.RegularExpressions;

namespace Vde.Shell;

public enum VolumeKind
{
    Internal,
    Removable,   // USB sticks, SD cards
    Optical,
    Network,
}

public sealed record Volume(
    string Device,        // /dev/sda1, or the remote source for network volumes
    string MountPoint,
    string FileSystem,
    string Label,         // what to show: the disk label, or a name made from the mount point
    VolumeKind Kind,
    long TotalBytes,
    long FreeBytes,
    bool ReadOnly);

// A network share that is currently mounted, e.g. smb://nas/photos.
public sealed record NetworkShare(string Name, string Url, string MountPoint, string FileSystem);

// "Computer": the drives that are mounted. "Network": the shares that are mounted.
// Both are read from the kernel mount table; unmounted drives and network discovery need udisks and
// avahi and are not covered here.
public sealed partial class Computer
{
    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smb3", "fuse.sshfs", "davfs", "fuse.davfs", "afs", "ncpfs",
    };

    private readonly string _mountInfo;
    private readonly string _labelDir;
    private readonly string _sysBlock;

    public Computer(
        string mountInfoPath = "/proc/self/mountinfo",
        string labelDirectory = "/dev/disk/by-label",
        string sysBlockDirectory = "/sys/class/block")
    {
        _mountInfo = mountInfoPath;
        _labelDir = labelDirectory;
        _sysBlock = sysBlockDirectory;
    }

    // One entry per physical device: bind mounts and btrfs subvolumes of one disk collapse into the shortest mount point.
    public IReadOnlyList<Volume> GetVolumes()
    {
        var labels = ReadLabels();
        var byDevice = new Dictionary<string, Volume>(StringComparer.Ordinal);

        foreach (var mount in ReadMounts())
        {
            if (!mount.Source.StartsWith("/dev/", StringComparison.Ordinal) || mount.FileSystem == "squashfs")
                continue;

            var device = ResolveDevice(mount.Source);
            var (total, free) = Space(mount.MountPoint);
            var kind = KindOf(device, mount.FileSystem);
            var label = labels.GetValueOrDefault(device) ?? LabelFromMountPoint(mount.MountPoint);
            var volume = new Volume(device, mount.MountPoint, mount.FileSystem, label, kind, total, free, mount.ReadOnly);

            if (!byDevice.TryGetValue(device, out var existing) || Prefer(volume, existing))
                byDevice[device] = volume;
        }

        return byDevice.Values
            .OrderBy(v => v.MountPoint == "/" ? 0 : 1)
            .ThenBy(v => v.Kind)
            .ThenBy(v => v.MountPoint, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<NetworkShare> GetNetworkShares()
        => ReadMounts()
            .Where(m => NetworkTypes.Contains(m.FileSystem))
            .Select(m => ToShare(m))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ---- mount table ----

    private sealed record Mount(string MountPoint, string FileSystem, string Source, bool ReadOnly);

    private IEnumerable<Mount> ReadMounts()
    {
        if (!File.Exists(_mountInfo))
            yield break;

        foreach (var line in File.ReadLines(_mountInfo))
        {
            // id parent major:minor root mountpoint options [optional fields] - fstype source superoptions
            int dash = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0)
                continue;
            var left = line[..dash].Split(' ');
            var right = line[(dash + 3)..].Split(' ');
            if (left.Length < 6 || right.Length < 2)
                continue;

            yield return new Mount(
                Unescape(left[4]), right[0], Unescape(right[1]),
                left[5].Split(',').Contains("ro"));
        }
    }

    // The kernel writes space, tab, newline and backslash as \040 \011 \012 \134.
    private static string Unescape(string s)
        => OctalEscape().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();

    // ---- labels, kinds, sizes ----

    private Dictionary<string, string> ReadLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_labelDir))
            return labels;

        foreach (var link in Directory.EnumerateFileSystemEntries(_labelDir))
        {
            var target = new FileInfo(link).LinkTarget;
            if (target is null)
                continue;
            var device = "/dev/" + Path.GetFileName(target);
            // udev escapes odd characters in label names as \xNN
            var name = HexEscape().Replace(Path.GetFileName(link), m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            labels[device] = name;
        }
        return labels;
    }

    [GeneratedRegex(@"\\x([0-9a-fA-F]{2})")]
    private static partial Regex HexEscape();

    // /dev/mapper/x is a symlink to /dev/dm-N; use the real name so label and sysfs lookups work.
    private static string ResolveDevice(string source)
    {
        try
        {
            var target = new FileInfo(source).LinkTarget;
            return target is null ? source : "/dev/" + Path.GetFileName(target);
        }
        catch (IOException)
        {
            return source;
        }
    }

    private VolumeKind KindOf(string device, string fileSystem)
    {
        var name = Path.GetFileName(device);
        if (name.StartsWith("sr", StringComparison.Ordinal) || fileSystem is "iso9660" or "udf")
            return VolumeKind.Optical;

        var disk = DiskOf(name);
        var flag = Path.Combine(_sysBlock, disk, "removable");
        if (File.Exists(flag) && File.ReadAllText(flag).Trim() == "1")
            return VolumeKind.Removable;
        return VolumeKind.Internal;
    }

    // sda1 -> sda, nvme0n1p2 -> nvme0n1, mmcblk0p1 -> mmcblk0
    private static string DiskOf(string partition)
    {
        var m = Regex.Match(partition, @"^(nvme\d+n\d+|mmcblk\d+|loop\d+)p\d+$");
        if (m.Success)
            return m.Groups[1].Value;
        m = Regex.Match(partition, @"^([a-z]+)\d+$");
        return m.Success ? m.Groups[1].Value : partition;
    }

    private static (long total, long free) Space(string mountPoint)
    {
        try
        {
            var drive = new DriveInfo(mountPoint);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }

    private static string LabelFromMountPoint(string mountPoint)
        => mountPoint == "/" ? "System" : Path.GetFileName(mountPoint.TrimEnd('/')) is { Length: > 0 } name ? name : mountPoint;

    // The root filesystem always wins; otherwise the shortest path (/home over /home/user/.steam bind mounts).
    private static bool Prefer(Volume candidate, Volume current)
        => candidate.MountPoint == "/" || (current.MountPoint != "/" && candidate.MountPoint.Length < current.MountPoint.Length);

    // ---- network ----

    private static NetworkShare ToShare(Mount m)
    {
        string url, name;
        switch (m.FileSystem)
        {
            case "cifs" or "smb3":
                var smb = m.Source.TrimStart('/');                       // //server/share
                url = "smb://" + smb;
                name = ShareName(smb.Split('/').Skip(1).LastOrDefault(), smb.Split('/')[0]);
                break;
            case "nfs" or "nfs4":
                int colon = m.Source.IndexOf(':');                        // server:/export/path
                var host = colon > 0 ? m.Source[..colon] : m.Source;
                var path = colon > 0 ? m.Source[(colon + 1)..] : string.Empty;
                url = "nfs://" + host + path;
                name = ShareName(Path.GetFileName(path.TrimEnd('/')), host);
                break;
            case "fuse.sshfs":
                int sep = m.Source.IndexOf(':');                          // user@host:/path
                var login = sep > 0 ? m.Source[..sep] : m.Source;
                var remote = sep > 0 ? m.Source[(sep + 1)..] : string.Empty;
                url = "sftp://" + login + (remote.StartsWith('/') ? remote : "/" + remote);
                name = ShareName(Path.GetFileName(remote.TrimEnd('/')), login[(login.IndexOf('@') + 1)..]);
                break;
            default:
                url = m.Source;
                name = ShareName(Path.GetFileName(m.MountPoint.TrimEnd('/')), m.Source);
                break;
        }
        return new NetworkShare(name, url, m.MountPoint, m.FileSystem);
    }

    private static string ShareName(string? share, string host)
        => string.IsNullOrEmpty(share) ? host : $"{share} on {host}";
}
