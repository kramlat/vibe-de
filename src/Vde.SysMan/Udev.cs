namespace Vde.SysMan;

public sealed record UdevEntry(string Id, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Tags, IReadOnlyList<string> Links)
{
    public string? this[string property] => Properties.GetValueOrDefault(property);
}

// udev's device database (/run/udev/data): one text file per device that udev has processed, with the
// properties its rules and hwdb produced: vendor and model names, device class, ids. This is the heavy lifting,
// done by udev already; SYSMAN reads the result and never probes hardware itself.
//
//   file names: "+pci:0000:00:02.0" (a bus device), "c189:0" / "b8:0" (a character or block device), "n2" (a network interface)
//   lines:      E:KEY=VALUE (property)  G:tag  S:symlink  I:time  W:watch  L:priority  V:version
public sealed class Udev
{
    private readonly string _dir;
    private Dictionary<string, UdevEntry>? _entries;

    public Udev(string root) => _dir = Path.Combine(root, "run/udev/data");

    public IReadOnlyCollection<UdevEntry> All => Entries.Values;

    // A bus device by subsystem and sysfs name, e.g. ("pci", "0000:00:02.0") or ("usb", "1-1").
    public UdevEntry? Device(string subsystem, string sysName) => Entries.GetValueOrDefault($"+{subsystem}:{sysName}");

    private Dictionary<string, UdevEntry> Entries => _entries ??= Load();

    private Dictionary<string, UdevEntry> Load()
    {
        var entries = new Dictionary<string, UdevEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(_dir))
            return entries;
        foreach (var file in Directory.EnumerateFiles(_dir))
            try
            {
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                var tags = new List<string>();
                var links = new List<string>();
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length < 3 || line[1] != ':') continue;
                    var rest = line[2..];
                    switch (line[0])
                    {
                        case 'E' when rest.IndexOf('=') is var eq and > 0: props[rest[..eq]] = rest[(eq + 1)..]; break;
                        case 'G': tags.Add(rest); break;
                        case 'S': links.AddRange(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)); break;
                    }
                }
                var id = Path.GetFileName(file);
                entries[id] = new UdevEntry(id, props, tags, links);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return entries;
    }
}
