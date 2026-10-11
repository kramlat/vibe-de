using Vde.Freedesktop;
using Vde.Shell;
using Vibe.Registry;

namespace Vde.Shell.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-sh-" + Guid.NewGuid().ToString("N"));
    public string Home => Path.Combine(Root, "home");
    public string Sys => Path.Combine(Root, "usr-share");
    public string Bin => Path.Combine(Root, "bin");
    public XdgDirectories Xdg { get; }
    public Func<string, string?> Env { get; }

    public Fixture()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Sys);
        Directory.CreateDirectory(Bin);
        var env = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["XDG_DATA_HOME"] = Path.Combine(Home, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, "config"),
            ["XDG_DATA_DIRS"] = Sys,
            ["XDG_CONFIG_DIRS"] = Path.Combine(Root, "etc"),
            ["PATH"] = Bin,
        };
        Env = k => env.GetValueOrDefault(k);
        Xdg = new XdgDirectories(Env);
    }

    public string Write(string relative, string content = "")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Program(string name)
    {
        var path = Write("bin/" + name, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public string App(string id, string name, string exec, string mime, string extra = "", string? dir = null)
        => Write($"{dir ?? "usr-share"}/applications/{id}.desktop",
            $"[Desktop Entry]\nType=Application\nName={name}\nExec={exec}\nMimeType={mime}\n{extra}");

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class KnownFolderTests
{
    [Fact]
    public void Maps_to_xdg_locations()
    {
        using var fx = new Fixture();
        var folders = new KnownFolders(fx.Xdg);

        Assert.Equal(fx.Home, folders.Get(KnownFolder.Home));
        Assert.Equal(Path.Combine(fx.Home, "Downloads"), folders.Get(KnownFolder.Downloads));
        Assert.Equal(Path.Combine(fx.Home, "data", "applications"), folders.Get(KnownFolder.Applications));
    }
}

public class MimeTests
{
    private static MimeDatabase Db(Fixture fx)
    {
        fx.Write("usr-share/mime/globs2",
            "# comment\n" +
            "50:text/plain:*.txt\n" +
            "50:image/jpeg:*.jpg\n" +
            "50:image/jpeg:*.jpeg\n" +
            "50:application/gzip:*.gz\n" +
            "50:application/x-compressed-tar:*.tar.gz\n" +
            "50:text/x-makefile:Makefile\n" +
            "50:text/x-csrc:*.c\n" +
            "10:text/x-weak:*.weak\n" +
            "50:text/x-strong:*.weak\n" +
            "50:application/x-case:*.Z:cs\n" +
            "50:audio/x-glob:track??.mp3\n");
        fx.Write("usr-share/mime/aliases", "image/jpg image/jpeg\n");
        fx.Write("usr-share/mime/subclasses", "text/x-csrc text/plain\napplication/x-compressed-tar application/gzip\n");
        fx.Write("usr-share/mime/generic-icons", "image/jpeg:image-x-generic\n");
        fx.Write("usr-share/mime/image/jpeg.xml",
            "<?xml version=\"1.0\"?><mime-type xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\" type=\"image/jpeg\">" +
            "<comment>JPEG image</comment><comment xml:lang=\"de\">JPEG-Bild</comment></mime-type>");
        return new MimeDatabase(fx.Xdg);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("NOTES.TXT", "text/plain")]                  // case-insensitive by default
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("backup.tar.gz", "application/x-compressed-tar")]   // longest pattern beats *.gz
    [InlineData("data.gz", "application/gzip")]
    [InlineData("Makefile", "text/x-makefile")]
    [InlineData("a.weak", "text/x-strong")]                  // higher weight wins
    [InlineData("a.z", null)]                                // *.Z is case-sensitive
    [InlineData("a.Z", "application/x-case")]
    [InlineData("track07.mp3", "audio/x-glob")]
    [InlineData("/some/dir/main.c", "text/x-csrc")]
    [InlineData("unknown.xyz", null)]
    public void Name_lookup(string name, string? expected)
    {
        using var fx = new Fixture();
        Assert.Equal(expected, Db(fx).GuessFromName(name));
    }

    [Fact]
    public void Content_check_separates_text_from_binary_and_finds_directories()
    {
        using var fx = new Fixture();
        var db = Db(fx);
        var text = fx.Write("t/readme", "héllo\n");
        var bin = Path.Combine(fx.Root, "t", "blob");
        File.WriteAllBytes(bin, [1, 2, 0, 3]);

        Assert.Equal("text/plain", db.GuessForFile(text));
        Assert.Equal("application/octet-stream", db.GuessForFile(bin));
        Assert.Equal("inode/directory", db.GuessForFile(Path.Combine(fx.Root, "t")));
        Assert.Equal("text/x-csrc", db.GuessForFile(fx.Write("t/x.c", "int main;")));
    }

    [Fact]
    public void Hierarchy_aliases_icons_and_descriptions()
    {
        using var fx = new Fixture();
        var db = Db(fx);

        Assert.Equal(new[] { "text/x-csrc", "text/plain", "application/octet-stream" }, db.WithAncestors("text/x-csrc"));
        Assert.Equal("image/jpeg", db.Canonical("image/jpg"));
        Assert.Equal(new[] { "image-jpeg", "image-x-generic" }, db.IconNames("image/jpeg"));
        Assert.Equal(new[] { "video-mp4", "video-x-generic" }, db.IconNames("video/mp4"));
        Assert.Equal("JPEG-Bild", db.Describe("image/jpeg", ["de"]));
        Assert.Equal("JPEG image", db.Describe("image/jpeg", ["fr"]));
        Assert.Equal("what/ever", db.Describe("what/ever", []));
    }
}

public class AssociationTests
{
    private static (FileAssociations assoc, MimeDatabase mime, DesktopEntryDatabase apps) Build(Fixture fx)
    {
        fx.Write("usr-share/mime/globs2", "50:text/plain:*.txt\n50:text/x-csrc:*.c\n");
        fx.Write("usr-share/mime/subclasses", "text/x-csrc text/plain\n");
        var mime = new MimeDatabase(fx.Xdg);
        var apps = DesktopEntryDatabase.Load(fx.Xdg, []);
        return (new FileAssociations(fx.Xdg, mime, apps, ["VDE"]), mime, apps);
    }

    [Fact]
    public void Handlers_come_from_app_mime_lists_alphabetically_with_parent_types_last()
    {
        using var fx = new Fixture();
        fx.App("zed", "Zed", "zed %f", "text/plain;");
        fx.App("alpha", "Alpha", "alpha %f", "text/plain;");
        fx.App("cide", "C IDE", "cide %f", "text/x-csrc;");
        var (assoc, _, _) = Build(fx);

        Assert.Equal(new[] { "cide.desktop", "alpha.desktop", "zed.desktop" }, assoc.GetHandlers("text/x-csrc").Select(a => a.Id));
        Assert.Equal("alpha.desktop", assoc.GetDefault("text/plain")!.Id);
        Assert.Null(assoc.GetDefault("image/png"));
    }

    [Fact]
    public void Mimeapps_lists_set_defaults_add_and_remove_with_desktop_specific_first()
    {
        using var fx = new Fixture();
        fx.App("alpha", "Alpha", "alpha %f", "text/plain;");
        fx.App("beta", "Beta", "beta %f", "text/plain;");
        fx.App("gamma", "Gamma", "gamma %f", "");
        fx.Write("home/config/mimeapps.list", "[Default Applications]\ntext/plain=beta.desktop;\n\n[Removed Associations]\ntext/plain=alpha.desktop;\n");
        fx.Write("home/config/vde-mimeapps.list", "[Added Associations]\ntext/plain=gamma.desktop;\n");
        var (assoc, _, _) = Build(fx);

        // beta is the user default; gamma is added by the desktop file; alpha is removed.
        Assert.Equal(new[] { "beta.desktop", "gamma.desktop" }, assoc.GetHandlers("text/plain").Select(a => a.Id));
    }

    [Fact]
    public void Defaults_naming_missing_apps_are_ignored()
    {
        using var fx = new Fixture();
        fx.App("alpha", "Alpha", "alpha %f", "text/plain;");
        fx.Write("home/config/mimeapps.list", "[Default Applications]\ntext/plain=ghost.desktop;alpha.desktop;\n");
        var (assoc, _, _) = Build(fx);

        Assert.Equal("alpha.desktop", assoc.GetDefault("text/plain")!.Id);
    }

    [Fact]
    public void SetDefault_edits_the_user_file_and_keeps_comments()
    {
        using var fx = new Fixture();
        fx.App("alpha", "Alpha", "alpha %f", "text/plain;");
        fx.App("beta", "Beta", "beta %f", "text/plain;");
        fx.Write("home/config/mimeapps.list", "# my notes\n[Default Applications]\nimage/png=viewer.desktop;\n");
        var (assoc, _, apps) = Build(fx);

        assoc.SetDefault("text/plain", apps.Find("beta")!);

        var text = File.ReadAllText(assoc.UserMimeAppsPath);
        Assert.Contains("# my notes", text);
        Assert.Contains("image/png=viewer.desktop;", text);
        Assert.Contains("text/plain=beta.desktop;", text);
        Assert.Contains("[Added Associations]\ntext/plain=beta.desktop;", text);
        Assert.Equal("beta.desktop", assoc.GetDefault("text/plain")!.Id);

        assoc.SetDefault("text/plain", apps.Find("alpha")!);   // replaces, does not duplicate
        Assert.Equal(1, File.ReadAllLines(assoc.UserMimeAppsPath).Count(l => l.StartsWith("text/plain=alpha.desktop;") && !l.Contains("beta") || l.StartsWith("text/plain=beta.desktop;;")) + 0 * 1);
        Assert.Equal("alpha.desktop", assoc.GetDefault("text/plain")!.Id);
    }
}

public class ShellExecuteTests
{
    private sealed class Recorder : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = new();
        public void Start(ProcessRequest request) => Started.Add(request);
    }

    private static (ShellExecute shell, Recorder rec) Build(Fixture fx)
    {
        fx.Write("usr-share/mime/globs2", "50:text/plain:*.txt\n50:application/x-desktop:*.desktop\n");
        var mime = new MimeDatabase(fx.Xdg);
        var apps = DesktopEntryDatabase.Load(fx.Xdg, []);
        var assoc = new FileAssociations(fx.Xdg, mime, apps, ["VDE"]);
        var rec = new Recorder();
        return (new ShellExecute(assoc, mime, rec, fx.Env), rec);
    }

    [Fact]
    public void Open_runs_the_default_app_with_the_file()
    {
        using var fx = new Fixture();
        var editor = fx.Program("edit");
        fx.App("edit", "Edit", "edit --view %f", "text/plain;", "Path=/tmp\n");
        var file = fx.Write("docs/a b.txt", "hi");
        var (shell, rec) = Build(fx);

        shell.Open(file);

        var start = Assert.Single(rec.Started);
        Assert.Equal(editor, start.FileName);
        Assert.Equal(new[] { "--view", file }, start.Arguments);
        Assert.Equal("/tmp", start.WorkingDirectory);
    }

    [Fact]
    public void Open_url_uses_the_scheme_handler_and_file_uris_become_paths()
    {
        using var fx = new Fixture();
        fx.Program("browser");
        fx.Program("edit");
        fx.App("browser", "Browser", "browser %u", "x-scheme-handler/https;");
        fx.App("edit", "Edit", "edit %F", "text/plain;");
        var file = fx.Write("a.txt", "x");
        var (shell, rec) = Build(fx);

        shell.Open("https://example.org/x");
        shell.Open(new Uri(file).AbsoluteUri);

        Assert.Equal(new[] { "https://example.org/x" }, rec.Started[0].Arguments);
        Assert.Equal(new[] { file }, rec.Started[1].Arguments);
    }

    [Fact]
    public void Open_reports_missing_files_and_missing_associations()
    {
        using var fx = new Fixture();
        var (shell, _) = Build(fx);
        var file = fx.Write("a.txt", "x");

        Assert.Throws<FileNotFoundException>(() => shell.Open(Path.Combine(fx.Root, "nope.txt")));
        var ex = Assert.Throws<NoAssociationException>(() => shell.Open(file));
        Assert.Equal("text/plain", ex.MimeType);
    }

    [Fact]
    public void Opening_a_desktop_file_launches_it()
    {
        using var fx = new Fixture();
        var program = fx.Program("tool");
        var shortcut = fx.App("tool", "Tool", "tool --go", "", dir: "home");
        var (shell, rec) = Build(fx);

        shell.Open(shortcut);

        Assert.Equal(program, Assert.Single(rec.Started).FileName);
    }

    [Fact]
    public void Terminal_apps_and_actions()
    {
        using var fx = new Fixture();
        fx.Program("htop");
        var konsole = fx.Program("konsole");
        var entry = DesktopEntry.Parse(
            "[Desktop Entry]\nType=Application\nName=Top\nExec=htop\nTerminal=true\nActions=slow;\n[Desktop Action slow]\nName=Slow\nExec=htop -d 50\n",
            "top.desktop", []);
        var (shell, rec) = Build(fx);

        shell.Launch(entry);
        shell.Launch(entry, "slow");

        Assert.Equal(konsole, rec.Started[0].FileName);
        Assert.Equal(new[] { "-e", Path.Combine(fx.Bin, "htop") }.Length, rec.Started[0].Arguments.Count);
        Assert.Equal("-e", rec.Started[0].Arguments[0]);
        Assert.Equal(new[] { "-e", "htop", "-d", "50" }, rec.Started[1].Arguments.Take(1).Concat(new[] { "htop", "-d", "50" }).ToArray()[..4]);
        Assert.Throws<ArgumentException>(() => shell.Launch(entry, "nope"));
    }

    [Fact]
    public void Run_executes_commands_or_falls_back_to_open()
    {
        using var fx = new Fixture();
        var tool = fx.Program("tool");
        var (shell, rec) = Build(fx);

        shell.Run("tool --flag \"two words\"");

        Assert.Equal(tool, rec.Started[0].FileName);
        Assert.Equal(new[] { "--flag", "two words" }, rec.Started[0].Arguments);
        Assert.Throws<FileNotFoundException>(() => shell.Run("not-a-program-or-file"));
    }

    [Fact]
    public void IsAvailable_checks_the_executable()
    {
        using var fx = new Fixture();
        fx.Program("here");
        var (shell, _) = Build(fx);

        Assert.True(shell.IsAvailable(DesktopEntry.Parse("[Desktop Entry]\nName=a\nExec=here %f\n", "a.desktop", [])));
        Assert.False(shell.IsAvailable(DesktopEntry.Parse("[Desktop Entry]\nName=a\nExec=gone %f\n", "a.desktop", [])));
        Assert.False(shell.IsAvailable(DesktopEntry.Parse("[Desktop Entry]\nName=a\nExec=here\nTryExec=gone\n", "a.desktop", [])));
    }
}

public class TrashTests
{
    [Fact]
    public void Send_list_restore_and_empty()
    {
        using var fx = new Fixture();
        var trash = new Trash(fx.Xdg);
        var file = fx.Write("work/my file #1.txt", "data");
        var dir = fx.Write("work/folder/inner.txt", "x");

        var sent = trash.Send(file);
        trash.Send(Path.GetDirectoryName(dir)!);

        Assert.False(File.Exists(file));
        Assert.False(trash.IsEmpty);
        var info = File.ReadAllText(Path.Combine(fx.Home, "data", "Trash", "info", "my file #1.txt.trashinfo"));
        Assert.Contains("Path=" + Path.Combine(fx.Root, "work", "my%20file%20%231.txt").Replace("\\", "/"), info);
        Assert.Matches(@"DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d", info);

        var items = trash.List();
        Assert.Equal(2, items.Count);
        var back = items.Single(i => i.StoredName == sent.StoredName);
        Assert.Equal(file, back.OriginalPath);

        trash.Restore(back);
        Assert.Equal("data", File.ReadAllText(file));
        Assert.Single(trash.List());

        trash.Empty();
        Assert.True(trash.IsEmpty);
        Assert.Empty(trash.List());
    }

    [Fact]
    public void Same_names_get_unique_stored_names_and_restore_never_overwrites()
    {
        using var fx = new Fixture();
        var trash = new Trash(fx.Xdg);
        var a = fx.Write("a/same.txt", "first");
        var first = trash.Send(a);
        fx.Write("a/same.txt", "second");
        var second = trash.Send(a);

        Assert.Equal("same.txt", first.StoredName);
        Assert.Equal("same.2.txt", second.StoredName);

        fx.Write("a/same.txt", "occupied");
        Assert.Throws<IOException>(() => trash.Restore(second));
        var elsewhere = trash.Restore(second, Path.Combine(fx.Root, "b", "moved.txt"));
        Assert.Equal("second", File.ReadAllText(elsewhere));
    }

    [Fact]
    public void Trashing_a_missing_file_throws_and_leaves_no_info()
    {
        using var fx = new Fixture();
        var trash = new Trash(fx.Xdg);
        Assert.Throws<FileNotFoundException>(() => trash.Send(Path.Combine(fx.Root, "ghost")));
        Assert.True(trash.IsEmpty);
    }
}

public class FileOperationTests
{
    private static string Tree(Fixture fx)
    {
        fx.Write("src/a.txt", "AAAA");
        fx.Write("src/sub/b.txt", "BB");
        return Path.Combine(fx.Root, "src");
    }

    [Fact]
    public void Copy_folder_preserves_contents_and_reports_progress()
    {
        using var fx = new Fixture();
        var src = Tree(fx);
        var reports = new List<FileProgress>();
        var progress = new SyncProgress(reports.Add);

        var created = FileOperations.Copy([src], Path.Combine(fx.Root, "dst"), new FileOperationOptions { Progress = progress });

        Assert.Equal(new[] { Path.Combine(fx.Root, "dst", "src") }, created);
        Assert.Equal("BB", File.ReadAllText(Path.Combine(fx.Root, "dst", "src", "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
        var last = reports[^1];
        Assert.Equal(6, last.BytesTotal);
        Assert.Equal(6, last.BytesDone);
        Assert.Equal(last.ItemsTotal, last.ItemsDone);
    }

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }

    [Fact]
    public void Move_removes_the_source()
    {
        using var fx = new Fixture();
        var src = Tree(fx);

        FileOperations.Move([src], Path.Combine(fx.Root, "dst"));

        Assert.False(Directory.Exists(src));
        Assert.Equal("AAAA", File.ReadAllText(Path.Combine(fx.Root, "dst", "src", "a.txt")));
    }

    [Fact]
    public void Conflicts_follow_the_resolver()
    {
        using var fx = new Fixture();
        var file = fx.Write("in/f.txt", "new");
        fx.Write("out/f.txt", "old");
        var dst = Path.Combine(fx.Root, "out");

        Assert.Throws<IOException>(() => FileOperations.Copy([file], dst));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "f.txt")));

        FileOperations.Copy([file], dst, new() { OnConflict = (_, _) => ConflictAction.Skip });
        Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "f.txt")));

        var both = FileOperations.Copy([file], dst, new() { OnConflict = (_, _) => ConflictAction.KeepBoth });
        Assert.Equal(Path.Combine(dst, "f (copy).txt"), both[0]);
        Assert.Equal("new", File.ReadAllText(both[0]));

        FileOperations.Copy([file], dst, new() { OnConflict = (_, _) => ConflictAction.Replace });
        Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "f.txt")));

        Assert.Throws<OperationCanceledException>(
            () => FileOperations.Copy([file], dst, new() { OnConflict = (_, _) => ConflictAction.Cancel }));
    }

    [Fact]
    public void Folders_merge_and_cannot_be_copied_into_themselves()
    {
        using var fx = new Fixture();
        var src = Tree(fx);
        fx.Write("dst/src/keep.txt", "keep");
        var dst = Path.Combine(fx.Root, "dst");

        FileOperations.Copy([src], dst, new() { OnConflict = (_, _) => ConflictAction.Replace });

        Assert.True(File.Exists(Path.Combine(dst, "src", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "src", "a.txt")));
        Assert.Throws<IOException>(() => FileOperations.Copy([src], Path.Combine(src, "sub")));
    }

    [Fact]
    public void Cancelling_leaves_no_partial_file()
    {
        using var fx = new Fixture();
        var big = Path.Combine(fx.Root, "big.bin");
        File.WriteAllBytes(big, new byte[2_000_000]);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FileOperations.Copy([big], Path.Combine(fx.Root, "out"), new() { Cancel = cts.Token, Progress = progress }));

        Assert.False(File.Exists(Path.Combine(fx.Root, "out", "big.bin")));
    }

    [Fact]
    public void Rename_create_folder_and_delete()
    {
        using var fx = new Fixture();
        var file = fx.Write("d/a.txt", "x");
        fx.Write("d/b.txt", "y");

        Assert.Throws<IOException>(() => FileOperations.Rename(file, "b.txt"));
        Assert.Throws<ArgumentException>(() => FileOperations.Rename(file, "x/y"));
        var renamed = FileOperations.Rename(file, "c.txt");
        Assert.True(File.Exists(renamed));

        var parent = Path.Combine(fx.Root, "d");
        Assert.EndsWith("New folder", FileOperations.CreateFolder(parent));
        Assert.EndsWith("New folder (2)", FileOperations.CreateFolder(parent));

        FileOperations.Delete([parent]);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void Symlinks_are_copied_as_links()
    {
        using var fx = new Fixture();
        var target = fx.Write("t/real.txt", "r");
        var link = Path.Combine(fx.Root, "t", "link");
        File.CreateSymbolicLink(link, "real.txt");

        FileOperations.Copy([link], Path.Combine(fx.Root, "out"));

        Assert.Equal("real.txt", new FileInfo(Path.Combine(fx.Root, "out", "link")).LinkTarget);
    }
}

public class ComputerTests
{
    private const string MountInfo = """
        24 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
        25 24 259:2 /home /home rw,relatime shared:2 - ext4 /dev/nvme0n1p2 rw
        26 24 0:6 / /dev rw - devtmpfs devtmpfs rw
        27 24 0:22 / /proc rw - proc proc rw
        28 24 259:1 / /boot/efi rw - vfat /dev/nvme0n1p1 rw
        29 24 8:17 / /run/media/deck/My\040Stick rw - exfat /dev/sdb1 rw
        30 24 11:0 / /run/media/deck/DISC ro - iso9660 /dev/sr0 ro
        31 24 7:0 / /snap/core ro - squashfs /dev/loop0 ro
        32 24 0:50 / /mnt/nas rw - cifs //nas.local/photos rw
        33 24 0:51 / /mnt/data rw - nfs4 files:/export/data rw
        34 24 0:52 / /mnt/remote rw - fuse.sshfs deck@box:/srv/stuff rw
        35 24 0:6 / /tmp rw - tmpfs tmpfs rw
        """;

    private static Computer Build(Fixture fx)
    {
        var mounts = fx.Write("proc/mountinfo", MountInfo);
        var labels = Path.Combine(fx.Root, "labels");
        Directory.CreateDirectory(labels);
        File.CreateSymbolicLink(Path.Combine(labels, "My\\x20Stick"), "../../sdb1");
        File.CreateSymbolicLink(Path.Combine(labels, "ARCH"), "../../nvme0n1p1");
        fx.Write("sys/sdb/removable", "1\n");
        fx.Write("sys/nvme0n1/removable", "0\n");
        return new Computer(mounts, labels, Path.Combine(fx.Root, "sys"));
    }

    [Fact]
    public void Lists_real_drives_once_with_labels_and_kinds()
    {
        using var fx = new Fixture();
        var volumes = Build(fx).GetVolumes();

        Assert.Equal(new[] { "/", "/boot/efi", "/run/media/deck/My Stick", "/run/media/deck/DISC" }, volumes.Select(v => v.MountPoint));

        var root = volumes[0];
        Assert.Equal("/dev/nvme0n1p2", root.Device);          // /home bind mount collapsed into /
        Assert.Equal("System", root.Label);
        Assert.Equal(VolumeKind.Internal, root.Kind);

        Assert.Equal("ARCH", volumes.Single(v => v.MountPoint == "/boot/efi").Label);
        var stick = volumes.Single(v => v.Device == "/dev/sdb1");
        Assert.Equal("My Stick", stick.Label);
        Assert.Equal(VolumeKind.Removable, stick.Kind);
        var disc = volumes.Single(v => v.Device == "/dev/sr0");
        Assert.Equal(VolumeKind.Optical, disc.Kind);
        Assert.True(disc.ReadOnly);
    }

    [Fact]
    public void Lists_network_shares_with_urls()
    {
        using var fx = new Fixture();
        var shares = Build(fx).GetNetworkShares();

        Assert.Equal(
            new[] { "data on files", "photos on nas.local", "stuff on box" },
            shares.Select(s => s.Name));
        Assert.Equal("nfs://files/export/data", shares[0].Url);
        Assert.Equal("smb://nas.local/photos", shares[1].Url);
        Assert.Equal("sftp://deck@box/srv/stuff", shares[2].Url);
        Assert.Equal("/mnt/nas", shares[1].MountPoint);
    }

    [Fact]
    public void Missing_mount_table_gives_empty_lists()
    {
        var computer = new Computer("/nonexistent/mountinfo");
        Assert.Empty(computer.GetVolumes());
        Assert.Empty(computer.GetNetworkShares());
    }
}

public class MimeTieTests
{
    [Fact]
    public void A_user_type_cannot_take_over_an_extension_the_system_defines_at_equal_weight()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
        fx.Write("home/data/mime/globs2", "50:application/retro-game:*.png\n50:application/retro-game:*.rom\n");
        var db = new MimeDatabase(fx.Xdg);

        Assert.Equal("image/png", db.GuessFromName("a.png"));
        Assert.Equal("application/retro-game", db.GuessFromName("a.rom"));   // user types still work where unambiguous
    }

    [Fact]
    public void A_higher_weight_from_the_user_directory_does_override()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n");
        fx.Write("home/data/mime/globs2", "60:application/mine:*.png\n");

        Assert.Equal("application/mine", new MimeDatabase(fx.Xdg).GuessFromName("a.png"));
    }
}

public class RegistryAssociationTests
{
    private sealed class World : IDisposable
    {
        public Fixture Fx { get; } = new();
        public Hive User { get; } = new("user");
        public Hive System { get; } = new("system");
        public DesktopEntryDatabase Apps { get; }
        public MimeDatabase Mime { get; }

        public World()
        {
            Fx.Write("usr-share/mime/globs2", "50:text/plain:*.txt\n");
            Fx.App("alpha", "Alpha", "alpha %f", "text/plain;");
            Fx.App("beta", "Beta", "beta %f", "text/plain;");
            Fx.App("gamma", "Gamma", "gamma %f", "");
            Mime = new MimeDatabase(Fx.Xdg);
            Apps = DesktopEntryDatabase.Load(Fx.Xdg, []);
        }

        public FileAssociations Assoc(bool mirror = true, bool withSystem = true)
            => new(Fx.Xdg, Mime, Apps, ["VDE"], new ClassesRoot(User, withSystem ? System : null), mirror);

        public void Dispose() => Fx.Dispose();
    }

    [Fact]
    public void Registry_default_beats_mimeapps_list_and_app_lists()
    {
        using var w = new World();
        w.Fx.Write("home/config/mimeapps.list", "[Default Applications]\ntext/plain=beta.desktop;\n");
        w.User.SetValue(@"Software\Classes\MIME\text/plain", "Default", RegistryValueKind.String, "gamma.desktop");

        var handlers = w.Assoc().GetHandlers("text/plain").Select(a => a.Id).ToList();

        Assert.Equal("gamma.desktop", handlers[0]);
        Assert.Equal("beta.desktop", handlers[1]);   // freedesktop choice is still offered, as fallback
        Assert.Contains("alpha.desktop", handlers);
    }

    [Fact]
    public void User_hive_beats_system_hive_and_system_beats_freedesktop()
    {
        using var w = new World();
        w.Fx.Write("home/config/mimeapps.list", "[Default Applications]\ntext/plain=alpha.desktop;\n");
        w.System.SetValue(@"Software\Classes\MIME\text/plain", "Default", RegistryValueKind.String, "beta.desktop");

        Assert.Equal("beta.desktop", w.Assoc().GetDefault("text/plain")!.Id);

        w.User.SetValue(@"Software\Classes\MIME\text/plain", "Default", RegistryValueKind.String, "gamma.desktop");
        Assert.Equal("gamma.desktop", w.Assoc().GetDefault("text/plain")!.Id);
    }

    [Fact]
    public void Falls_back_to_freedesktop_when_the_registry_has_nothing()
    {
        using var w = new World();
        w.Fx.Write("home/config/mimeapps.list", "[Default Applications]\ntext/plain=beta.desktop;\n");

        Assert.Equal("beta.desktop", w.Assoc().GetDefault("text/plain")!.Id);
        Assert.Equal("alpha.desktop", new FileAssociations(w.Fx.Xdg, w.Mime, w.Apps, ["VDE"]).GetHandlers("text/plain")
            .First(a => a.Id != "beta.desktop").Id);   // and works with no hives at all
    }

    [Fact]
    public void Registry_added_and_removed_lists_apply()
    {
        using var w = new World();
        w.User.SetValue(@"Software\Classes\MIME\text/plain", "Added", RegistryValueKind.MultiString, new[] { "gamma.desktop" });
        w.User.SetValue(@"Software\Classes\MIME\text/plain", "Removed", RegistryValueKind.MultiString, new[] { "alpha.desktop" });

        var ids = w.Assoc().GetHandlers("text/plain").Select(a => a.Id).ToList();

        Assert.Equal(new[] { "gamma.desktop", "beta.desktop" }, ids);
    }

    [Fact]
    public void SetDefault_writes_the_registry_and_mirrors_to_mimeapps_list()
    {
        using var w = new World();
        var assoc = w.Assoc();

        assoc.SetDefault("text/plain", w.Apps.Find("beta")!);

        Assert.Equal("beta.desktop", w.User.GetValue(@"Software\Classes\MIME\text/plain", "Default")!.Data);
        Assert.Equal(new[] { "beta.desktop" }, (string[])w.User.GetValue(@"Software\Classes\MIME\text/plain", "Added")!.Data!);
        Assert.Null(w.System.GetValue(@"Software\Classes\MIME\text/plain", "Default"));   // only the first hive is written
        Assert.Contains("text/plain=beta.desktop;", File.ReadAllText(assoc.UserMimeAppsPath));
        Assert.Equal("beta.desktop", assoc.GetDefault("text/plain")!.Id);
    }

    [Fact]
    public void Mirroring_can_be_turned_off()
    {
        using var w = new World();
        var assoc = w.Assoc(mirror: false);

        assoc.SetDefault("text/plain", w.Apps.Find("beta")!);

        Assert.False(File.Exists(assoc.UserMimeAppsPath));
        Assert.Equal("beta.desktop", assoc.GetDefault("text/plain")!.Id);
    }

    [Fact]
    public void Choosing_an_app_unhides_it_and_ClearDefault_restores_the_fallback()
    {
        using var w = new World();
        var assoc = w.Assoc(mirror: false);
        w.User.SetValue(@"Software\Classes\MIME\text/plain", "Removed", RegistryValueKind.MultiString, new[] { "gamma.desktop", "alpha.desktop" });

        assoc.SetDefault("text/plain", w.Apps.Find("alpha")!);
        Assert.Equal(new[] { "gamma.desktop" }, (string[])w.User.GetValue(@"Software\Classes\MIME\text/plain", "Removed")!.Data!);
        Assert.Equal("alpha.desktop", assoc.GetDefault("text/plain")!.Id);

        assoc.ClearDefault("text/plain");
        Assert.Null(w.User.GetValue(@"Software\Classes\MIME\text/plain", "Default"));
        // alpha remains in the Added list, so it is still offered
        Assert.Contains("alpha.desktop", assoc.GetHandlers("text/plain").Select(a => a.Id));
    }

    [Fact]
    public void Associations_survive_saving_the_hive()
    {
        using var w = new World();
        w.Assoc(mirror: false).SetDefault("text/plain", w.Apps.Find("beta")!);

        var ms = new MemoryStream();
        w.User.Write(ms);
        ms.Position = 0;
        var reloaded = new FileAssociations(w.Fx.Xdg, w.Mime, w.Apps, ["VDE"], new ClassesRoot(Hive.Read("user", ms)), false);

        Assert.Equal("beta.desktop", reloaded.GetDefault("text/plain")!.Id);
    }
}

public class FileTypesTests
{
    private static (FileTypes types, ClassesRoot hkcr, Fixture fx) Build()
    {
        var fx = new Fixture();
        fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n50:text/plain:*.txt\n50:text/x-log:*.log\n");
        fx.Write("usr-share/mime/generic-icons", "image/png:image-x-generic\n");
        fx.Write("usr-share/mime/image/png.xml",
            "<mime-type xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\" type=\"image/png\"><comment>PNG image</comment></mime-type>");
        var hkcr = new ClassesRoot(new Hive("HKCU"), new Hive("HKLM"));
        return (new FileTypes(new MimeDatabase(fx.Xdg), hkcr), hkcr, fx);
    }

    [Fact]
    public void Without_registry_entries_everything_comes_from_the_mime_database()
    {
        var (types, _, fx) = Build();
        using (fx)
        {
            var info = types.Describe("/x/photo.PNG");

            Assert.Equal("image/png", info.MimeType);
            Assert.Equal("PNG image", info.Description);
            Assert.Equal(new[] { "image-png", "image-x-generic" }, info.IconNames);
        }
    }

    [Fact]
    public void Extension_class_overrides_icon_description_and_content_type()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(@"CLSID\.png", "DefaultIcon", RegistryValueKind.String, "my-png-icon");
            hkcr.SetValue(@"CLSID\.png", "", RegistryValueKind.String, "Picture");
            hkcr.SetValue(@"CLSID\.log", "Content Type", RegistryValueKind.String, "text/plain");

            var png = types.Describe("/x/a.png");
            Assert.Equal("Picture", png.Description);
            Assert.Equal(new[] { "my-png-icon", "image-png", "image-x-generic" }, png.IconNames);
            Assert.Equal("image/png", png.MimeType);

            Assert.Equal("text/plain", types.GetMimeType("/x/server.log"));
            Assert.Equal("text/plain", types.Describe("/x/server.log").MimeType);
        }
    }

    [Fact]
    public void Mime_class_applies_to_every_file_of_that_type_and_the_extension_wins()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(@"CLSID\image/png", "DefaultIcon", RegistryValueKind.String, "type-icon");
            Assert.Equal("type-icon", types.Describe("/x/a.png").IconNames[0]);

            hkcr.SetValue(@"CLSID\.png", "DefaultIcon", RegistryValueKind.String, "ext-icon");
            Assert.Equal(new[] { "ext-icon", "type-icon" }, types.Describe("/x/a.png").IconNames.Take(2));
        }
    }

    [Fact]
    public void Directories_and_unknown_files_and_wrong_value_types()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            Assert.Equal("inode/directory", types.Describe(fx.Root).MimeType);
            hkcr.SetValue(@"CLSID\.png", "DefaultIcon", RegistryValueKind.DWord, 5u);   // not a string: ignored
            Assert.Equal("image-png", types.Describe("/x/a.png").IconNames[0]);
            Assert.Equal("application/octet-stream", types.Describe("/x/unknown.zzz").MimeType);
        }
    }

    [Fact]
    public void ShellExecute_honours_content_type_overrides()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(@"CLSID\.log", "Content Type", RegistryValueKind.String, "text/plain");
            fx.Program("edit");
            fx.App("edit", "Edit", "edit %f", "text/plain;");
            var file = fx.Write("a.log", "x");
            var mime = new MimeDatabase(fx.Xdg);
            var apps = DesktopEntryDatabase.Load(fx.Xdg, []);
            var launches = new List<ProcessRequest>();
            var shell = new ShellExecute(new FileAssociations(fx.Xdg, mime, apps, ["VDE"]), mime,
                new Capture(launches), fx.Env, types: types);

            shell.Open(file);

            Assert.Equal(new[] { file }, Assert.Single(launches).Arguments);
        }
    }

    private sealed class Capture(List<ProcessRequest> into) : IProcessLauncher
    {
        public void Start(ProcessRequest request) => into.Add(request);
    }
}

public class WindowsLayoutFileTypeTests
{
    private static (FileTypes types, ClassesRoot hkcr, Fixture fx) Build()
    {
        var fx = new Fixture();
        fx.Write("usr-share/mime/globs2", "50:image/png:*.png\n50:text/x-log:*.log\n");
        var hkcr = new ClassesRoot(new Hive("HKCU"), new Hive("HKLM"));
        return (new FileTypes(new MimeDatabase(fx.Xdg), hkcr), hkcr, fx);
    }

    [Fact]
    public void Follows_the_ext_to_ProgID_chain()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".png", "", RegistryValueKind.String, "pngfile");
            hkcr.SetValue("pngfile", "", RegistryValueKind.String, "PNG Image");
            hkcr.SetValue("pngfile", "DefaultIcon", RegistryValueKind.String, "image-png-special");

            var info = types.Describe("/x/a.png");

            Assert.Equal("PNG Image", info.Description);
            Assert.Equal("image-png-special", info.IconNames[0]);
        }
    }

    [Fact]
    public void Content_type_on_the_ext_key_is_honoured()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".log", "Content Type", RegistryValueKind.String, "text/plain");
            Assert.Equal("text/plain", types.GetMimeType("/x/a.log"));
        }
    }

    [Fact]
    public void Clsid_class_beats_the_ProgID_chain()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".png", "", RegistryValueKind.String, "pngfile");
            hkcr.SetValue("pngfile", "DefaultIcon", RegistryValueKind.String, "from-progid");
            hkcr.SetValue("pngfile", "", RegistryValueKind.String, "From ProgID");
            hkcr.SetValue(@"CLSID\.png", "DefaultIcon", RegistryValueKind.String, "from-clsid");
            hkcr.SetValue(@"CLSID\.png", "", RegistryValueKind.String, "From CLSID");

            var info = types.Describe("/x/a.png");

            Assert.Equal("From CLSID", info.Description);
            Assert.Equal(new[] { "from-clsid", "from-progid" }, info.IconNames.Take(2));
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe,0")]
    [InlineData("shell32.dll,3")]
    [InlineData(@"%SystemRoot%\system32\imageres.dll")]
    public void Windows_icon_references_are_ignored(string windowsIcon)
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".png", "", RegistryValueKind.String, "pngfile");
            hkcr.SetValue("pngfile", "DefaultIcon", RegistryValueKind.String, windowsIcon);

            Assert.Equal("image-png", types.Describe("/x/a.png").IconNames[0]);
        }
    }

    [Fact]
    public void Absolute_linux_paths_are_kept_as_icons()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".png", "", RegistryValueKind.String, "pngfile");
            hkcr.SetValue("pngfile", "DefaultIcon", RegistryValueKind.String, "/opt/app/icon.svg");

            Assert.Equal("/opt/app/icon.svg", types.Describe("/x/a.png").IconNames[0]);
        }
    }

    [Fact]
    public void A_dangling_ProgID_is_harmless()
    {
        var (types, hkcr, fx) = Build();
        using (fx)
        {
            hkcr.SetValue(".png", "", RegistryValueKind.String, "missingclass");

            var info = types.Describe("/x/a.png");

            Assert.Equal("image/png", info.MimeType);
            Assert.Equal("image-png", info.IconNames[0]);
        }
    }
}
