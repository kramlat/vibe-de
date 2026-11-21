using Vde.Shell;
using Vibe.Registry;

namespace Dosdevman;

// dosdevman: drive letters (DOS devices) for this user. The commands change the map in the registry and make it real; the daemon (a
// systemd user service) keeps it real while drives come and go.
public static class DosdevmanCommand
{
    public const string Usage = """
        dosdevman: drive letters for block devices, folders and the main Wine bottle.
        usage: dosdevman list
               dosdevman assign <device|mount point|label> [letter]   give a mounted drive a letter (the next free one by default)
               dosdevman mount <folder> [letter] [--label NAME]       a folder as a drive letter
               dosdevman bottle [prefix]                              show, or set, the Wine prefix whose drive_c is C:
               dosdevman bottles add|remove <prefix>                  Wine prefixes kept in step with the letters
               dosdevman unassign <letter>
               dosdevman sync                                         write the links Wine reads, now
               dosdevman daemon                                       keep them in step as drives come and go (run by systemd)
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error, DosDevices dos, IHive? hive = null, string? dosdevices = null, CancellationToken cancel = default)
    {
        dosdevices ??= DosDevices.DefaultDirectory;
        if (args.Length == 0 || args[0] is "-h" or "--help") { output.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        try
        {
            switch (args[0])
            {
                case "list": return List(dos, output);
                case "assign" when args.Length is 2 or 3: return Assign(dos, args, output, error, dosdevices);
                case "mount" when args.Length >= 2: return Mount(dos, args, output, dosdevices);
                case "bottle" when args.Length is 1 or 2: return Bottle(dos, args, output, dosdevices);
                case "bottles" when args.Length == 3 && args[1] is "add" or "remove": return Bottles(dos, args, output);
                case "unassign" when args.Length == 2:
                    dos.Unassign(DosLetters.Parse(args[1]) ?? throw new ArgumentException($"\"{args[1]}\" is not a drive letter."));
                    return Synced(dos, output, dosdevices);
                case "sync": return Synced(dos, output, dosdevices);
                case "daemon": return new DosdevmanDaemon(dos, hive, dosdevices, error).RunAsync(cancel).GetAwaiter().GetResult();
                default: error.WriteLine(Usage); return 2;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("dosdevman: " + e.Message);
            return 1;
        }
    }

    private static int List(DosDevices dos, TextWriter output)
    {
        foreach (var d in dos.Drives())
        {
            var what = d.Mapping.Kind switch { DosKind.Device => "device", DosKind.WineRoot => "Wine", _ => "folder" };
            var where = d.Path ?? (d.Mapping.Kind == DosKind.Device ? "(not mounted)" : "(missing)");
            output.WriteLine($"{d.Root,-3} {what,-7} {(d.Mapping.Label.Length > 0 ? d.Mapping.Label : "-"),-20} {where}");
        }
        if (dos.Store.MainBottle is { } bottle) output.WriteLine($"main bottle: {bottle}");
        return 0;
    }

    private static int Assign(DosDevices dos, string[] args, TextWriter output, TextWriter error, string dosdevices)
    {
        var volumes = new Computer().GetVolumes();
        var wanted = args[1];
        var found = volumes.Where(v => v.Device == wanted || v.MountPoint == wanted.TrimEnd('/') || string.Equals(v.Label, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1) { error.WriteLine(found.Count == 0 ? $"dosdevman: no mounted drive matches \"{wanted}\"." : $"dosdevman: \"{wanted}\" matches {found.Count} drives; use the device name."); return 1; }
        var letter = args.Length == 3 ? DosLetters.Parse(args[2]) ?? throw new ArgumentException($"\"{args[2]}\" is not a drive letter.") : (char?)null;
        var assigned = dos.Assign(found[0], letter);
        output.WriteLine($"{found[0].Device} is {assigned}:");
        return Synced(dos, output, dosdevices);
    }

    private static int Mount(DosDevices dos, string[] args, TextWriter output, string dosdevices)
    {
        string label = ""; char? letter = null; string? folder = null;
        for (var i = 1; i < args.Length; i++)
            if (args[i] == "--label" && i + 1 < args.Length) label = args[++i];
            else if (folder is null) folder = args[i];
            else letter = DosLetters.Parse(args[i]) ?? throw new ArgumentException($"\"{args[i]}\" is not a drive letter.");
        var assigned = dos.MountFolder(folder!, letter, label);
        output.WriteLine($"{Path.GetFullPath(folder!)} is {assigned}:");
        return Synced(dos, output, dosdevices);
    }

    private static int Bottle(DosDevices dos, string[] args, TextWriter output, string dosdevices)
    {
        if (args.Length == 1) { output.WriteLine(dos.Store.MainBottle ?? "(no main bottle)"); return 0; }
        dos.SetMainBottle(args[1]);
        output.WriteLine($"C: is {Path.Combine(Path.GetFullPath(args[1]), "drive_c")}");
        return Synced(dos, output, dosdevices);
    }

    private static int Bottles(DosDevices dos, string[] args, TextWriter output)
    {
        var prefix = Path.GetFullPath(args[2]);
        var now = dos.Store.Bottles.ToList();
        if (args[1] == "add") { if (!Directory.Exists(Path.Combine(prefix, "dosdevices"))) throw new DirectoryNotFoundException($"\"{prefix}\" is not a Wine prefix (no dosdevices folder)."); now.Add(prefix); }
        else now.Remove(prefix);
        dos.Store.SetBottles(now);
        output.WriteLine(string.Join('\n', dos.Store.Bottles));
        return 0;
    }

    private static int Synced(DosDevices dos, TextWriter output, string dosdevices)
    {
        foreach (var line in dos.SyncAll(dosdevices)) output.WriteLine(line);
        return 0;
    }
}
