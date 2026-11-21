using Dosdevman;
using Vde.Shell;
using Vibe.Registry;

namespace Dosdevman.Tests;

public class DosdevmanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dosdev-" + Guid.NewGuid().ToString("N"));
    private readonly Hive _hive = new("HKCU");
    private readonly List<Volume> _volumes = [];

    public DosdevmanTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Volume Usb(string device, string mount, string label) => new(device, mount, "vfat", label, VolumeKind.Removable, 1000, 500, false);

    private DosDevices Dos() => new(new DosDeviceStore(_hive), () => _volumes, device => "uuid:" + device.Replace("/dev/", ""));

    private string Folder(string name) { var p = Path.Combine(_root, name); Directory.CreateDirectory(p); return p; }

    [Fact]
    public void A_drive_gets_the_next_free_letter_keeps_it_and_a_letter_can_be_chosen()
    {
        var dos = Dos();
        var stick = Usb("/dev/sdb1", Folder("media/stick"), "STICK");
        var card = Usb("/dev/mmcblk0p1", Folder("media/card"), "CARD");
        _volumes.AddRange([stick, card]);

        Assert.Equal('D', dos.Assign(stick));
        Assert.Equal('D', dos.Assign(stick));                    // asked again: the same letter
        Assert.Equal('E', dos.Assign(card));
        Assert.Equal('D', dos.LetterOf(stick));
        Assert.Equal('G', dos.Assign(stick, 'g'));                // moved on request, the old letter is free again
        Assert.Equal('D', dos.NextFree());
        Assert.Throws<InvalidOperationException>(() => dos.Assign(card, 'G'));   // taken
        Assert.Throws<InvalidOperationException>(() => dos.Assign(card, 'Z'));   // Z: is the root
        Assert.Throws<InvalidOperationException>(() => dos.Assign(card, 'C'));   // C: is the main bottle
    }

    [Fact]
    public void A_folder_is_mounted_as_a_drive_and_the_main_bottles_drive_c_is_C()
    {
        var dos = Dos();
        var games = Folder("games");
        var prefix = Folder("wine/main"); Directory.CreateDirectory(Path.Combine(prefix, "drive_c")); Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));

        Assert.Equal('D', dos.MountFolder(games, label: "Games"));
        dos.SetMainBottle(prefix);
        var drives = dos.Drives();
        Assert.Equal(Path.Combine(prefix, "drive_c"), drives.Single(d => d.Letter == 'C').Path);   // C: is the bottle's drive_c
        Assert.Equal(games, drives.Single(d => d.Letter == 'D').Path);
        Assert.Equal(prefix, dos.Store.MainBottle);
        Assert.Throws<DirectoryNotFoundException>(() => dos.SetMainBottle(Folder("not-a-prefix")));    // no drive_c

        dos.Unassign('C');
        Assert.Null(dos.Store.MainBottle);
    }

    [Fact]
    public void Sync_writes_the_links_wine_reads_and_never_touches_what_is_not_its_own()
    {
        var dos = Dos();
        var stick = Usb("/dev/sdb1", Folder("media/stick"), "STICK");
        _volumes.Add(stick);
        dos.Assign(stick);                                         // D:
        var games = Folder("games"); dos.MountFolder(games, 'F');  // F:
        var prefix = Folder("wine/main"); Directory.CreateDirectory(Path.Combine(prefix, "drive_c")); dos.SetMainBottle(prefix);
        var dd = Path.Combine(prefix, "dosdevices"); Directory.CreateDirectory(dd);
        File.CreateSymbolicLink(Path.Combine(dd, "c:"), "../drive_c");
        File.CreateSymbolicLink(Path.Combine(dd, "z:"), "/");
        File.WriteAllText(Path.Combine(dd, "f:"), "a real file");   // in the way: left alone

        var log = dos.Sync(dd, prefix: true);

        Assert.Equal(stick.MountPoint, new FileInfo(Path.Combine(dd, "d:")).LinkTarget);        // d: -> where the stick is
        Assert.Equal("/dev/sdb1", new FileInfo(Path.Combine(dd, "d::")).LinkTarget);             // d:: -> the device, so wine knows the drive type
        Assert.Equal("../drive_c", new FileInfo(Path.Combine(dd, "c:")).LinkTarget);             // the prefix's own c: is not touched
        Assert.Equal("/", new FileInfo(Path.Combine(dd, "z:")).LinkTarget);
        Assert.Equal("a real file", File.ReadAllText(Path.Combine(dd, "f:")));
        Assert.True(log.Any(l => l.Contains("f: alone, it is not a link")), string.Join(" | ", log));

        // the stick is unplugged: its links go away, and only ones dosdevman wrote
        _volumes.Clear();
        dos.Sync(dd, prefix: true);
        Assert.False(File.Exists(Path.Combine(dd, "d:")) || new FileInfo(Path.Combine(dd, "d:")).LinkTarget is not null);
        Assert.Equal("../drive_c", new FileInfo(Path.Combine(dd, "c:")).LinkTarget);
    }

    [Fact]
    public async Task The_tool_lists_assigns_mounts_and_unassigns_and_the_daemon_syncs_when_the_map_changes()
    {
        var dos = Dos();
        var dd = Path.Combine(_root, "vde-dosdevices");
        var games = Folder("games");
        string Run(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var code = DosdevmanCommand.Run(args, o, e, dos, _hive, dd);
            Assert.True(code == 0, e.ToString());
            return o.ToString();
        }

        Assert.Contains(" is D:", Run("mount", games, "--label", "Games"));
        Assert.Contains("Games", Run("list"));
        Assert.Equal(games, new FileInfo(Path.Combine(dd, "d:")).LinkTarget);

        Run("unassign", "d:");
        Assert.Null(new FileInfo(Path.Combine(dd, "d:")).LinkTarget);
        var bad = new StringWriter();
        Assert.Equal(1, DosdevmanCommand.Run(["unassign", "?"], new StringWriter(), bad, dos, _hive, dd));
        Assert.Contains("not a drive letter", bad.ToString());

        // the daemon: a letter set in the registry becomes a link without anyone running sync
        using var cancel = new CancellationTokenSource();
        var daemon = new DosdevmanDaemon(dos, _hive, dd, new StringWriter()) { Poll = TimeSpan.FromMilliseconds(20) };
        var task = daemon.RunAsync(cancel.Token);
        dos.MountFolder(games, 'H');
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (new FileInfo(Path.Combine(dd, "h:")).LinkTarget is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        cancel.Cancel();
        Assert.Equal(0, await task);
        Assert.Equal(games, new FileInfo(Path.Combine(dd, "h:")).LinkTarget);
    }

    [Theory]
    [InlineData("d", 'D')] [InlineData("D:", 'D')] [InlineData("e:\\", 'E')] [InlineData("z:/", 'Z')]
    [InlineData("", null)] [InlineData("dd", null)] [InlineData("1:", null)] [InlineData("d:x", null)]
    public void Drive_letters_are_read_from_d_d_colon_or_d_colon_backslash(string text, char? expected)
        => Assert.Equal(expected, DosLetters.Parse(text));
}
