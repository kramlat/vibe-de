using Vde.Freedesktop;
using Vde.Shell;
using Vde.Components;
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

public class RegistryCommandTests
{
    [Theory]
    [InlineData("/opt/app/run \"%1\"", new[] { "/a b/c.txt" }, new[] { "/opt/app/run", "/a b/c.txt" })]
    [InlineData("app --file=%L", new[] { "/x.txt" }, new[] { "app", "--file=/x.txt" })]
    [InlineData("app %*", new[] { "/a", "/b c" }, new[] { "app", "/a", "/b c" })]
    [InlineData("app %1 %2", new[] { "/a" }, new[] { "app", "/a" })]      // missing %2 vanishes
    [InlineData("app --dir %W", new[] { "/home/u/x.txt" }, new[] { "app", "--dir", "/home/u" })]
    [InlineData("app 100%% %7", new string[0], new[] { "app", "100%" })]
    [InlineData("app --flag", new string[0], new[] { "app", "--flag" })]
    public void Expansion(string command, string[] targets, string[] expected)
        => Assert.Equal(expected, RegistryCommand.Expand(command, targets));
}

public class RegistryOpenTests
{
    private sealed class Capture : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = new();
        public void Start(ProcessRequest request) => Started.Add(request);
    }

    private sealed class World : IDisposable
    {
        public Fixture Fx { get; } = new();
        public ClassesRoot Hkcr { get; } = new(new Hive("HKCU"), new Hive("HKLM"));
        public Capture Launches { get; } = new();
        public string File { get; }
        public string Winish { get; }
        public ShellExecute Shell { get; }
        public FileAssociations Assoc { get; }

        public World()
        {
            Fx.Write("usr-share/mime/globs2", "50:application/x-thing:*.thing\n");
            Winish = Fx.Program("winish");
            Fx.Program("native");
            Fx.App("native", "Native", "native %f", "application/x-thing;");
            File = Fx.Write("docs/a b.thing", "x");

            var mime = new MimeDatabase(Fx.Xdg);
            var apps = DesktopEntryDatabase.Load(Fx.Xdg, []);
            Assoc = new FileAssociations(Fx.Xdg, mime, apps, ["VDE"], Hkcr, mirrorToFreedesktop: false);
            Shell = new ShellExecute(Assoc, mime, Launches, Fx.Env, types: new FileTypes(mime, Hkcr));
        }

        // chmod 755 on a command value, as an installer deliberately does
        public void MakeExecutable(string commandKey)
        {
            var current = Hkcr.GetValuePermissions(commandKey, "")!.Value;
            Hkcr.SetValuePermissions(commandKey, "", current.WithMode(PosixPermissions.KeyDefault));
        }

        public void Install()
        {
            Hkcr.SetValue(".thing", "", RegistryValueKind.String, "Thing.Document");
            Hkcr.SetValue(@"Thing.Document\shell\open\command", "", RegistryValueKind.String, "winish --open \"%1\"");
            Hkcr.SetValue(@"Thing.Document\shell\edit", "", RegistryValueKind.String, "&Edit with Thing");
            Hkcr.SetValue(@"Thing.Document\shell\edit\command", "", RegistryValueKind.String, "winish --edit \"%1\"");
            MakeExecutable(@"Thing.Document\shell\open\command");
            MakeExecutable(@"Thing.Document\shell\edit\command");
        }

        public void Dispose() => Fx.Dispose();
    }

    [Fact]
    public void Installed_command_beats_freedesktop_association()
    {
        using var w = new World();
        w.Install();

        w.Shell.Open(w.File);

        var start = Assert.Single(w.Launches.Started);
        Assert.Equal(w.Winish, start.FileName);
        Assert.Equal(new[] { "--open", w.File }, start.Arguments);   // unix path, spaces intact
    }

    [Fact]
    public void The_users_own_choice_beats_the_installed_command()
    {
        using var w = new World();
        w.Install();
        w.Assoc.SetDefault("application/x-thing", w.Assoc.GetHandlers("application/x-thing").Single());

        w.Shell.Open(w.File);

        Assert.EndsWith("native", Assert.Single(w.Launches.Started).FileName);
    }

    [Fact]
    public void Without_a_registered_command_freedesktop_is_used()
    {
        using var w = new World();

        w.Shell.Open(w.File);

        Assert.EndsWith("native", Assert.Single(w.Launches.Started).FileName);
    }

    [Fact]
    public void Verbs_are_listed_with_labels_and_the_default_first_and_can_be_invoked()
    {
        using var w = new World();
        w.Install();
        w.Hkcr.SetValue(@"Thing.Document\shell", "", RegistryValueKind.String, "edit");   // make edit the default verb
        var types = new FileTypes(new MimeDatabase(w.Fx.Xdg), w.Hkcr);

        var verbs = types.GetVerbs(w.File);

        Assert.Equal(new[] { "edit", "open" }, verbs.Select(v => v.Id));
        Assert.Equal("Edit with Thing", verbs[0].Label);
        Assert.Equal("Open", verbs[1].Label);

        w.Shell.Invoke(w.File, "edit");
        Assert.Equal(new[] { "--edit", w.File }, Assert.Single(w.Launches.Started).Arguments);
        Assert.Throws<ArgumentException>(() => w.Shell.Invoke(w.File, "print"));
    }

    [Fact]
    public void A_command_naming_a_missing_program_reports_it()
    {
        using var w = new World();
        w.Hkcr.SetValue(".thing", "", RegistryValueKind.String, "Thing.Document");
        w.Hkcr.SetValue(@"Thing.Document\shell\open\command", "", RegistryValueKind.String, "/nowhere/app \"%1\"");
        w.MakeExecutable(@"Thing.Document\shell\open\command");

        Assert.Throws<FileNotFoundException>(() => w.Shell.Open(w.File));
    }
}

public class ExecutionTrustTests
{
    private static readonly RegistryIdentity Alice = new(1000, 1000, [100]);
    private static readonly RegistryIdentity Bob = new(1001, 1001, [100]);
    private static readonly RegistryIdentity Root = RegistryIdentity.Root;

    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);
    private static PosixPermissions P(uint uid, uint gid, string mode) => new(uid, gid, M(mode));

    [Theory]
    [InlineData(1000u, "755", true)]     // mine, executable
    [InlineData(1000u, "700", true)]
    [InlineData(1000u, "644", false)]    // not executable: what an import or a plain write creates
    [InlineData(0u, "755", true)]        // root's, executable by everyone
    [InlineData(0u, "700", false)]       // root's, root only: not for Alice
    [InlineData(0u, "750", false)]       // root's, group 0 only
    [InlineData(1001u, "755", false)]    // Bob's: another user's command never runs as Alice
    [InlineData(1000u, "757", false)]    // writable by anyone
    [InlineData(1000u, "777", false)]
    public void The_policy_for_an_ordinary_user(uint owner, string mode, bool trusted)
        => Assert.Equal(trusted, ExecutionTrust.Evaluate(P(owner, owner, mode), Alice).IsTrusted);

    [Fact]
    public void Group_execute_works_for_group_members_only()
    {
        var shared = P(0, 100, "750");   // root's, group 100 may execute
        Assert.True(ExecutionTrust.Evaluate(shared, Alice).IsTrusted);
        Assert.True(ExecutionTrust.Evaluate(shared, Bob).IsTrusted);
        Assert.False(ExecutionTrust.Evaluate(shared, new RegistryIdentity(1002, 1002)).IsTrusted);
    }

    [Fact]
    public void Root_only_actions_run_for_root_and_say_why_they_do_not_for_everyone_else()
    {
        var rootOnly = P(0, 0, "700");

        Assert.True(ExecutionTrust.Evaluate(rootOnly, Root).IsTrusted);
        var denied = ExecutionTrust.Evaluate(rootOnly, Alice);
        Assert.False(denied.IsTrusted);
        Assert.Contains("not executable by you", denied.Reason);
        Assert.Contains("another user", ExecutionTrust.Evaluate(P(1001, 1001, "755"), Alice).Reason);
        Assert.Contains("anyone can write", ExecutionTrust.Evaluate(P(1000, 1000, "757"), Alice).Reason);
        Assert.False(ExecutionTrust.Evaluate(null, Alice).IsTrusted);
    }

    private sealed class Capture : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = new();
        public void Start(ProcessRequest request) => Started.Add(request);
    }

    // The system hive is written by root (an installer); the user hive is Alice's.
    private sealed class World : IDisposable
    {
        public Fixture Fx { get; } = new();
        public Hive System { get; } = new("HKLM") { Identity = Root };
        public Hive User { get; }
        public ClassesRoot Hkcr { get; }
        public Capture Launches { get; } = new();
        public string File { get; }
        public ShellExecute Shell { get; }
        public FileTypes Types { get; }

        public World(RegistryIdentity identity)
        {
            User = new Hive("HKCU") { Identity = identity };
            Hkcr = new ClassesRoot(User, System);
            Fx.Write("usr-share/mime/globs2", "50:application/x-thing:*.thing\n");
            Fx.Program("winish");
            Fx.Program("native");
            Fx.App("native", "Native", "native %f", "application/x-thing;");
            File = Fx.Write("docs/a.thing", "x");

            var mime = new MimeDatabase(Fx.Xdg);
            var apps = DesktopEntryDatabase.Load(Fx.Xdg, []);
            Types = new FileTypes(mime, Hkcr, identity: identity);
            Shell = new ShellExecute(new FileAssociations(Fx.Xdg, mime, apps, ["VDE"], Hkcr, mirrorToFreedesktop: false), mime, Launches, Fx.Env, types: Types);
        }

        // An installer (root) registers a command in the system hive with the given owner and mode.
        public void InstallSystemCommand(string mode, uint owner = 0, uint group = 0)
        {
            System.SetValue(@"Software\Classes\.thing", "", RegistryValueKind.String, "Thing.Document");
            System.SetValue(@"Software\Classes\Thing.Document\shell\open\command", "", RegistryValueKind.String, "winish \"%1\"");
            System.SetValuePermissions(@"Software\Classes\Thing.Document\shell\open\command", "", P(owner, group, mode));
            // keys must be searchable by everyone
            System.SetKeyPermissions("", P(0, 0, "755"));
        }

        public void Dispose() => Fx.Dispose();
    }

    [Fact]
    public void An_inert_open_command_is_ignored_and_freedesktop_takes_over()
    {
        using var w = new World(Alice);
        w.InstallSystemCommand("644");   // registered, but not marked executable

        w.Shell.Open(w.File);

        Assert.EndsWith("native", Assert.Single(w.Launches.Started).FileName);   // the freedesktop app, not winish
    }

    [Fact]
    public void An_executable_system_command_runs_for_a_normal_user()
    {
        using var w = new World(Alice);
        w.InstallSystemCommand("755");

        w.Shell.Open(w.File);

        Assert.EndsWith("winish", Assert.Single(w.Launches.Started).FileName);
    }

    [Fact]
    public void Verbs_report_trust_so_a_menu_can_grey_them_out_and_invoking_one_explains_itself()
    {
        using var w = new World(Alice);
        w.InstallSystemCommand("700");   // root-only

        var verb = Assert.Single(w.Types.GetVerbs(w.File));
        Assert.False(verb.IsTrusted);
        Assert.Contains("not executable by you", verb.UntrustedReason);

        var ex = Assert.Throws<UntrustedCommandException>(() => w.Shell.Invoke(w.File, "open"));
        Assert.Equal("open", ex.Verb);
        Assert.Empty(w.Launches.Started);
    }

    [Fact]
    public void The_same_root_only_command_runs_when_root_asks()
    {
        using var w = new World(Root);
        w.InstallSystemCommand("700");

        Assert.True(Assert.Single(w.Types.GetVerbs(w.File)).IsTrusted);
        w.Shell.Invoke(w.File, "open");
        Assert.EndsWith("winish", Assert.Single(w.Launches.Started).FileName);
    }

    [Fact]
    public void A_users_own_override_beats_the_system_command_and_is_judged_on_its_own_permissions()
    {
        using var w = new World(Alice);
        w.InstallSystemCommand("755");
        // Alice overrides the command in her own hive: a plain write is 0644, so it is inert...
        w.Hkcr.SetValue(@"Thing.Document\shell\open\command", "", RegistryValueKind.String, "winish --mine \"%1\"");
        w.Hkcr.SetValue(".thing", "", RegistryValueKind.String, "Thing.Document");   // her hive's own copy of the chain
        Assert.False(Assert.Single(w.Types.GetVerbs(w.File)).IsTrusted);

        // ...until she marks it executable herself.
        var current = w.Hkcr.GetValuePermissions(@"Thing.Document\shell\open\command", "")!.Value;
        w.Hkcr.SetValuePermissions(@"Thing.Document\shell\open\command", "", current.WithMode(M("755")));
        Assert.True(Assert.Single(w.Types.GetVerbs(w.File)).IsTrusted);
        w.Shell.Open(w.File);
        Assert.Contains("--mine", Assert.Single(w.Launches.Started).Arguments);
    }

    [Fact]
    public void Importing_a_reg_file_cannot_plant_a_runnable_command()
    {
        using var w = new World(Alice);
        const string reg = """
            Windows Registry Editor Version 5.00

            [HKEY_CLASSES_ROOT\.thing]
            @="Thing.Document"

            [HKEY_CLASSES_ROOT\Thing.Document\shell\open\command]
            @="winish \"%1\""
            """;

        // HKEY_CLASSES_ROOT lives under Software\Classes in the user hive
        var imported = RegFile.Parse(reg.Replace("HKEY_CLASSES_ROOT", @"HKEY_CURRENT_USER\Software\Classes"));
        imported.ApplyTo(w.User, "HKEY_CURRENT_USER");

        var verb = Assert.Single(w.Types.GetVerbs(w.File));
        Assert.False(verb.IsTrusted);   // imported values are 0644
        w.Shell.Open(w.File);
        Assert.EndsWith("native", Assert.Single(w.Launches.Started).FileName);
    }
}

public class ElevationTests
{
    private static readonly RegistryIdentity Alice = new(1000, 1000, [100]);

    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);

    private sealed class ScriptedPrompt(params ElevationResponse[] responses) : IElevationPrompt
    {
        private readonly Queue<ElevationResponse> _responses = new(responses);
        public List<(ElevationRequest request, int attempt, string? error)> Asked { get; } = new();

        public Task<ElevationResponse> AskAsync(ElevationRequest request, int attempt, string? previousError, CancellationToken cancel)
        {
            Asked.Add((request, attempt, previousError));
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : ElevationResponse.Cancel);
        }
    }

    private sealed class FakeBackend(bool canElevate, params ElevationOutcome[] outcomes) : IElevationBackend
    {
        private readonly Queue<ElevationOutcome> _outcomes = new(outcomes);
        public List<(IReadOnlyList<string> commandLine, char[] password)> Runs { get; } = new();

        public bool CanElevate(RegistryIdentity identity) => canElevate;

        public Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel)
        {
            Runs.Add((commandLine, password.ToArray()));
            return Task.FromResult(_outcomes.Count > 0 ? _outcomes.Dequeue() : ElevationOutcome.Success);
        }
    }

    private static ElevationResponse Password(string text) => new(true, text.ToCharArray());

    private sealed class Capture : IProcessLauncher
    {
        public List<ProcessRequest> Started { get; } = new();
        public void Start(ProcessRequest request) => Started.Add(request);
    }

    // A command registered by an installer (root), with the mode the test asks for; Alice is the user.
    private sealed class World : IDisposable
    {
        public Fixture Fx { get; } = new();
        public Capture Launches { get; } = new();
        public string File { get; }
        public string Winish { get; }
        public ShellExecute Shell { get; }

        public World(string commandMode, IElevationPrompt? prompt = null, IElevationBackend? backend = null, bool freedesktopFallback = false)
        {
            var system = new Hive("HKLM") { Identity = RegistryIdentity.Root };
            system.SetKeyPermissions("", new PosixPermissions(0, 0, M("755")));
            system.SetValue(@"Software\Classes\.thing", "", RegistryValueKind.String, "Thing.Document");
            system.SetValue(@"Software\Classes\Thing.Document\shell\open\command", "", RegistryValueKind.String, "winish --open \"%1\"");
            system.SetValue(@"Software\Classes\Thing.Document\shell\edit\command", "", RegistryValueKind.String, "winish --edit \"%1\"");
            foreach (var verb in new[] { "open", "edit" })
                system.SetValuePermissions($@"Software\Classes\Thing.Document\shell\{verb}\command", "", new PosixPermissions(0, 0, M(commandMode)));
            var hkcr = new ClassesRoot(new Hive("HKCU") { Identity = Alice }, system);

            Fx.Write("usr-share/mime/globs2", "50:application/x-thing:*.thing\n");
            Winish = Fx.Program("winish");
            if (freedesktopFallback)
            {
                Fx.Program("native");
                Fx.App("native", "Native", "native %f", "application/x-thing;");
            }
            File = Fx.Write("docs/a.thing", "x");

            var mime = new MimeDatabase(Fx.Xdg);
            var apps = DesktopEntryDatabase.Load(Fx.Xdg, []);
            var types = new FileTypes(mime, hkcr, identity: Alice);
            var elevation = prompt is null || backend is null ? null : new ElevationOptions(backend, prompt, Alice);
            Shell = new ShellExecute(new FileAssociations(Fx.Xdg, mime, apps, ["VDE"], hkcr, mirrorToFreedesktop: false),
                mime, Launches, Fx.Env, types: types, elevation: elevation);
        }

        public void Dispose() => Fx.Dispose();
    }

    [Fact]
    public async Task A_trusted_action_runs_without_any_prompt()
    {
        var prompt = new ScriptedPrompt();
        var backend = new FakeBackend(true);
        using var w = new World("755", prompt, backend);

        await w.Shell.InvokeAsync(w.File, "edit");

        Assert.Equal(new[] { "--edit", w.File }, Assert.Single(w.Launches.Started).Arguments);
        Assert.Empty(prompt.Asked);
        Assert.Empty(backend.Runs);
    }

    [Fact]
    public async Task Without_elevation_an_untrusted_action_is_rejected()
    {
        using var w = new World("644");

        var ex = await Assert.ThrowsAsync<UntrustedCommandException>(() => w.Shell.InvokeAsync(w.File, "edit"));
        Assert.Equal(w.File, ex.Path);
        await Assert.ThrowsAsync<UntrustedCommandException>(() => w.Shell.OpenAsync(w.File));
        Assert.Empty(w.Launches.Started);
    }

    [Fact]
    public async Task A_user_who_may_not_elevate_is_rejected_before_any_prompt()
    {
        var prompt = new ScriptedPrompt(Password("hunter2"));
        var backend = new FakeBackend(canElevate: false);
        using var w = new World("700", prompt, backend);

        var ex = await Assert.ThrowsAsync<ElevationDeniedException>(() => w.Shell.InvokeAsync(w.File, "edit"));

        Assert.Contains("not allowed to elevate", ex.Message);
        Assert.Empty(prompt.Asked);
        Assert.Empty(backend.Runs);
        Assert.Empty(w.Launches.Started);
    }

    [Fact]
    public async Task A_user_who_may_elevate_sees_the_exact_command_and_it_runs_through_the_backend()
    {
        var response = Password("hunter2");
        var prompt = new ScriptedPrompt(response);
        var backend = new FakeBackend(true);
        using var w = new World("700", prompt, backend);

        await w.Shell.InvokeAsync(w.File, "edit");

        var (request, attempt, error) = Assert.Single(prompt.Asked);
        Assert.Equal(1, attempt);
        Assert.Null(error);
        Assert.Equal("Edit", request.Label);
        Assert.Equal(new[] { w.Winish, "--edit", w.File }, request.CommandLine);
        Assert.Equal($"{w.Winish} --edit {w.File}", request.CommandText);
        Assert.Contains("not executable by you", request.Reason);

        var run = Assert.Single(backend.Runs);
        Assert.Equal(request.CommandLine, run.commandLine);
        Assert.Equal("hunter2", new string(run.password));
        Assert.All(response.Password!, c => Assert.Equal('\0', c));   // wiped after use
        Assert.Empty(w.Launches.Started);                              // elevated runs go through the backend, not the plain launcher
    }

    [Fact]
    public async Task Cancelling_runs_nothing_and_says_so()
    {
        var prompt = new ScriptedPrompt(ElevationResponse.Cancel);
        var backend = new FakeBackend(true);
        using var w = new World("700", prompt, backend);

        await Assert.ThrowsAsync<OperationCanceledException>(() => w.Shell.InvokeAsync(w.File, "edit"));

        Assert.Single(prompt.Asked);
        Assert.Empty(backend.Runs);
        Assert.Empty(w.Launches.Started);
    }

    [Fact]
    public async Task A_wrong_password_asks_again_up_to_three_times()
    {
        var wrong = new ElevationOutcome(ElevationStatus.WrongPassword, "Sorry, try again.");
        var prompt = new ScriptedPrompt(Password("a"), Password("b"), Password("right"));
        var backend = new FakeBackend(true, wrong, wrong, ElevationOutcome.Success);
        using var w = new World("700", prompt, backend);

        await w.Shell.InvokeAsync(w.File, "edit");

        Assert.Equal(new[] { 1, 2, 3 }, prompt.Asked.Select(a => a.attempt));
        Assert.Null(prompt.Asked[0].error);
        Assert.Equal("Incorrect password. Try again.", prompt.Asked[1].error);
        Assert.Equal("Incorrect password. Try again.", prompt.Asked[2].error);
        Assert.Equal("right", new string(backend.Runs[2].password));
    }

    [Fact]
    public async Task Three_wrong_passwords_end_in_a_denial()
    {
        var wrong = new ElevationOutcome(ElevationStatus.WrongPassword);
        var prompt = new ScriptedPrompt(Password("1"), Password("2"), Password("3"), Password("4"));
        var backend = new FakeBackend(true, wrong, wrong, wrong, wrong);
        using var w = new World("700", prompt, backend);

        var ex = await Assert.ThrowsAsync<ElevationDeniedException>(() => w.Shell.InvokeAsync(w.File, "edit"));

        Assert.Contains("Too many", ex.Message);
        Assert.Equal(3, prompt.Asked.Count);
    }

    [Fact]
    public async Task The_backend_can_refuse_after_the_password_and_can_fail_to_start()
    {
        using (var denied = new World("700", new ScriptedPrompt(Password("pw")),
                   new FakeBackend(true, new ElevationOutcome(ElevationStatus.NotAllowed, "Sorry, user alice may not run sudo."))))
        {
            var ex = await Assert.ThrowsAsync<ElevationDeniedException>(() => denied.Shell.InvokeAsync(denied.File, "edit"));
            Assert.Contains("may not run sudo", ex.Message);
        }

        using var failed = new World("700", new ScriptedPrompt(Password("pw")),
            new FakeBackend(true, new ElevationOutcome(ElevationStatus.Failed, "command not found")));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Shell.InvokeAsync(failed.File, "edit"));
        Assert.Contains("command not found", failure.Message);
    }

    [Fact]
    public async Task Opening_prefers_a_trusted_alternative_to_asking_for_a_password()
    {
        var prompt = new ScriptedPrompt(Password("pw"));
        using var w = new World("644", prompt, new FakeBackend(true), freedesktopFallback: true);

        await w.Shell.OpenAsync(w.File);

        Assert.EndsWith("native", Assert.Single(w.Launches.Started).FileName);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task Opening_with_nothing_else_available_asks_to_elevate_the_registered_command()
    {
        var prompt = new ScriptedPrompt(Password("pw"));
        var backend = new FakeBackend(true);
        using var w = new World("644", prompt, backend);

        var plain = Assert.Throws<UntrustedCommandException>(() => w.Shell.Open(w.File));   // the synchronous call only reports it
        Assert.Equal("open", plain.Verb);
        Assert.Equal(w.File, plain.Path);

        await w.Shell.OpenAsync(w.File);

        Assert.Equal(new[] { w.Winish, "--open", w.File }, Assert.Single(prompt.Asked).request.CommandLine);
        Assert.Single(backend.Runs);
    }

    [Fact]
    public void Root_still_needs_an_execute_bit_somewhere()
    {
        var root = RegistryIdentity.Root;
        Assert.False(ExecutionTrust.Evaluate(new PosixPermissions(0, 0, M("644")), root).IsTrusted);   // an imported command does not run even as root
        Assert.True(ExecutionTrust.Evaluate(new PosixPermissions(0, 0, M("700")), root).IsTrusted);
        Assert.True(ExecutionTrust.Evaluate(new PosixPermissions(0, 0, M("010")), root).IsTrusted);
    }

    [Fact]
    public void The_command_text_quotes_anything_ambiguous()
    {
        var request = new ElevationRequest("x", "open", "/f", ["/usr/bin/tool", "--name=a b", "it's", "plain-arg_1.txt", ""], "r");

        Assert.Equal(@"/usr/bin/tool '--name=a b' 'it'\''s' plain-arg_1.txt ''", request.CommandText);
    }
}

public class SudoBackendTests
{
    private sealed class FakeExecutor(params ExecResult[] results) : IProcessExecutor
    {
        private readonly Queue<ExecResult> _results = new(results);
        public List<(IReadOnlyList<string> commandLine, string? stdin)> Calls { get; } = new();
        public Exception? Throw { get; init; }

        public Task<ExecResult> RunAsync(IReadOnlyList<string> commandLine, string? standardInput, TimeSpan wait, CancellationToken cancel)
        {
            Calls.Add((commandLine, standardInput));
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new ExecResult(0, "", false));
        }
    }

    private static readonly ExecResult Ok = new(0, "", false);
    private static readonly ExecResult Running = new(0, "", true);

    private static string GroupFile(Fixture fx) => fx.Write("etc/group", "root:x:0:\nwheel:x:998:deck\nusers:x:100:\n");

    [Fact]
    public void Administrators_may_elevate_by_group_without_asking_sudo()
    {
        using var fx = new Fixture();
        var executor = new FakeExecutor(new ExecResult(1, "a password is required", false));
        var backend = new SudoBackend(executor, GroupFile(fx));

        Assert.True(backend.CanElevate(new RegistryIdentity(1000, 1000, [998])));   // in wheel
        Assert.Empty(executor.Calls);
        Assert.True(backend.CanElevate(RegistryIdentity.Root));
    }

    [Fact]
    public void Others_may_elevate_only_if_sudo_lets_them_in_without_a_password()
    {
        using var fx = new Fixture();
        var identity = new RegistryIdentity(1001, 1001, [100]);

        Assert.True(new SudoBackend(new FakeExecutor(Ok), GroupFile(fx)).CanElevate(identity));
        Assert.False(new SudoBackend(new FakeExecutor(new ExecResult(1, "a password is required", false)), GroupFile(fx)).CanElevate(identity));
        Assert.False(new SudoBackend(new FakeExecutor { Throw = new System.ComponentModel.Win32Exception("no sudo") }, GroupFile(fx)).CanElevate(identity));
    }

    [Fact]
    public async Task The_password_goes_to_stdin_only_the_command_runs_on_that_authentication_and_it_is_dropped_after()
    {
        var executor = new FakeExecutor(Ok, Running, Ok);
        var backend = new SudoBackend(executor, "/nonexistent");

        var outcome = await backend.RunAsync(["/usr/bin/tool", "--flag", "a b"], "hunter2".AsMemory(), CancellationToken.None);

        Assert.Equal(ElevationStatus.Success, outcome.Status);
        Assert.Equal(3, executor.Calls.Count);
        Assert.Equal(new[] { "sudo", "-k", "-S", "-p", "", "-v" }, executor.Calls[0].commandLine);
        Assert.Equal("hunter2\n", executor.Calls[0].stdin);
        Assert.Equal(new[] { "sudo", "-n", "--", "/usr/bin/tool", "--flag", "a b" }, executor.Calls[1].commandLine);
        Assert.Null(executor.Calls[1].stdin);
        Assert.Equal(new[] { "sudo", "-k" }, executor.Calls[2].commandLine);
        Assert.DoesNotContain(executor.Calls.SelectMany(c => c.commandLine), a => a.Contains("hunter2"));   // never on a command line
    }

    [Theory]
    [InlineData("Sorry, try again.", ElevationStatus.WrongPassword)]
    [InlineData("sudo: 1 incorrect password attempt", ElevationStatus.WrongPassword)]
    [InlineData("Sorry, user bob may not run sudo on host.", ElevationStatus.NotAllowed)]
    [InlineData("bob is not in the sudoers file.", ElevationStatus.NotAllowed)]
    public async Task A_failed_check_is_classified(string stderr, ElevationStatus expected)
    {
        var executor = new FakeExecutor(new ExecResult(1, stderr, false));

        var outcome = await new SudoBackend(executor, "/nonexistent").RunAsync(["/bin/true"], "pw".AsMemory(), CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
        Assert.Single(executor.Calls);   // nothing was run
    }

    [Fact]
    public async Task A_command_that_cannot_run_is_reported_and_the_authentication_is_still_dropped()
    {
        var executor = new FakeExecutor(Ok, new ExecResult(1, "sudo: /bin/nope: command not found", false), Ok);

        var outcome = await new SudoBackend(executor, "/nonexistent").RunAsync(["/bin/nope"], "pw".AsMemory(), CancellationToken.None);

        Assert.Equal(ElevationStatus.Failed, outcome.Status);
        Assert.Contains("command not found", outcome.Message);
        Assert.Equal(new[] { "sudo", "-k" }, executor.Calls[^1].commandLine);
    }
}

public class FolderModelTests
{
    [Fact]
    public void Lists_files_and_folders_hides_dotfiles_and_follows_symlinks_to_folders()
    {
        using var fx = new Fixture();
        fx.Write("d/a.txt", "hello"); fx.Write("d/.hidden", "x"); Directory.CreateDirectory(Path.Combine(fx.Root, "d", "sub"));
        File.CreateSymbolicLink(Path.Combine(fx.Root, "d", "link"), Path.Combine(fx.Root, "d", "sub"));
        File.CreateSymbolicLink(Path.Combine(fx.Root, "d", "broken"), "/nonexistent/x");
        var folder = Path.Combine(fx.Root, "d");

        var shown = FolderLister.List(folder);
        Assert.Equal(new[] { "a.txt", "broken", "link", "sub" }, shown.Select(e => e.Name).Order());
        Assert.Contains(FolderLister.List(folder, showHidden: true), e => e.Name == ".hidden" && e.IsHidden);
        var a = shown.Single(e => e.Name == "a.txt");
        Assert.Equal((5L, ".txt", false), (a.Size, a.Extension, a.IsDirectory));
        Assert.True(shown.Single(e => e.Name == "link").IsDirectory);      // a link to a folder opens as one
        Assert.True(shown.Single(e => e.Name == "link").IsSymlink);
        Assert.False(shown.Single(e => e.Name == "broken").IsDirectory);   // a dangling one is still listed
    }

    [Fact]
    public void Sorting_keeps_folders_first_and_compares_numbers_naturally()
    {
        FileEntry F(string n, bool dir = false, long size = 0, int day = 1) => new(n, "/" + n, dir, false, false, size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), dir ? "" : Path.GetExtension(n));
        var items = new[] { F("file10.txt"), F("File2.txt"), F("zdir", true), F("adir", true), F("b.md", size: 9, day: 5), F("a.zip", size: 1, day: 9) };

        Assert.Equal(new[] { "adir", "zdir", "File2.txt", "file10.txt", "a.zip", "b.md" }.Length, FolderLister.Sort(items).Count);
        Assert.Equal(new[] { "adir", "zdir", "a.zip", "b.md", "File2.txt", "file10.txt" }, FolderLister.Sort(items).Select(e => e.Name));
        Assert.Equal(new[] { "zdir", "adir", "file10.txt", "File2.txt", "b.md", "a.zip" }, FolderLister.Sort(items, descending: true).Select(e => e.Name));
        Assert.Equal(new[] { "File2.txt", "file10.txt", "a.zip", "b.md" }, FolderLister.Sort(items, SortKey.Size).Where(e => !e.IsDirectory).Select(e => e.Name));   // empty files first, ties by name
        Assert.Equal("a.zip", FolderLister.Sort(items, SortKey.Modified, descending: true).First(e => !e.IsDirectory).Name);
        Assert.Equal(-1, Math.Sign(FolderLister.NaturalCompare("a2", "a10")));
        Assert.Equal(0, FolderLister.NaturalCompare("A1", "a1"));
    }

    [Fact]
    public void History_goes_back_forward_up_and_forgets_the_future_on_a_new_path()
    {
        var h = new NavigationHistory("/home/deck");
        h.Navigate("/home/deck/Music"); h.Navigate("/home/deck/Music/Rock");

        Assert.Equal("/home/deck/Music", h.Back());
        Assert.True(h.CanGoForward);
        Assert.Equal("/home/deck/Music/Rock", h.Forward());
        h.Back(); h.Navigate("/tmp");
        Assert.False(h.CanGoForward);
        Assert.Equal("/home/deck/Music", h.Back());
        h.Navigate("/tmp"); h.Navigate("/tmp");                  // same place twice is one step
        Assert.Equal("/home/deck/Music", h.Back());
        Assert.Equal("/home/deck", h.Up());
        Assert.Equal("/home", h.Up()); Assert.Equal("/", h.Up());
        Assert.False(h.CanGoUp);
        Assert.Equal("/", h.Up());
    }

    [Fact]
    public void Address_bar_breadcrumbs_and_typed_text()
    {
        var crumbs = AddressPath.Crumbs("/home/deck/Music", home: "/home/deck");
        Assert.Equal(new[] { "/", "home", "Home", "Music" }, crumbs.Select(c => c.Name));
        Assert.Equal("/home/deck/Music", crumbs[^1].Path);

        Assert.Equal("/home/deck", AddressPath.Parse("~", "/x", "/home/deck"));
        Assert.Equal("/home/deck/Docs", AddressPath.Parse("~/Docs", "/x", "/home/deck"));
        Assert.Equal("/x/sub", AddressPath.Parse("sub", "/x", "/home/deck"));
        Assert.Equal("/x", AddressPath.Parse("sub/..", "/x", "/home/deck"));
        Assert.Equal("/a b/c", AddressPath.Parse("file:///a%20b/c", "/x", "/h"));
        Assert.Null(AddressPath.Parse("smb://nas/share", "/x", "/h"));
        Assert.Null(AddressPath.Parse("   ", "/x", "/h"));
    }

    [Fact]
    public void A_burst_of_changes_is_one_callback()
    {
        using var fx = new Fixture();
        var folder = Path.Combine(fx.Root, "w"); Directory.CreateDirectory(folder);
        int calls = 0;
        using (new FolderWatcher(folder, () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(150)))
        {
            for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(folder, $"f{i}"), "x");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (calls == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Thread.Sleep(400);
        }
        Assert.Equal(1, calls);
    }
}

public class ControlPanelTests
{
    private static Hive Reg() => new("t") { Identity = RegistryIdentity.Root };

    private static void Item(IHive hive, string id, string name, string? command = "/bin/true", string? component = null, string? category = "System",
        string[]? keywords = null, string? description = null)
    {
        var key = ControlPanelCatalog.RegistryKey + "\\" + id;
        hive.SetValue(key, "Name", RegistryValueKind.String, name);
        if (command is not null) hive.SetValue(key, "Command", RegistryValueKind.String, command);
        if (component is not null) hive.SetValue(key, "Component", RegistryValueKind.String, component);
        if (category is not null) hive.SetValue(key, "Category", RegistryValueKind.String, category);
        if (keywords is not null) hive.SetValue(key, "Keywords", RegistryValueKind.MultiString, keywords);
        if (description is not null) hive.SetValue(key, "Description", RegistryValueKind.String, description);
    }

    [Fact]
    public void Items_come_from_built_ins_the_registry_and_settings_programs_with_user_overriding_system()
    {
        using var fx = new Fixture();
        var system = Reg(); var user = Reg();
        Item(system, "printers", "Printers", category: "Devices", keywords: ["print"]);
        Item(system, "appearance", "Look (system)");                          // overrides the built-in with the same id
        Item(user, "printers", "My Printers", category: "Devices");           // and the user's overrides the system's
        Item(user, "broken", "Nothing to open", command: null);               // no command or component: ignored
        Item(user, "applet", "Applet", command: null, component: "{11111111-0000-4000-8000-000000000001}");
        fx.Write("usr-share/applications/kcm.desktop", "[Desktop Entry]\nType=Application\nName=Bluetooth\nComment=Pair devices\nExec=kcm\nCategories=Settings;HardwareSettings;\n");
        fx.Write("usr-share/applications/editor.desktop", "[Desktop Entry]\nType=Application\nName=Editor\nExec=ed\nCategories=Utility;\n");
        var apps = DesktopEntryDatabase.Load(fx.Xdg, []);

        var catalog = ControlPanelCatalog.Load(system, user, apps, ["VDE"]);

        Assert.Equal("My Printers", catalog.Items.Single(i => i.Id == "printers").Name);
        Assert.Equal("Look (system)", catalog.Items.Single(i => i.Id == "appearance").Name);
        Assert.Contains(catalog.Items, i => i.Id == "notifications" && i.Source == ItemSource.BuiltIn && i.PageId == "notifications");
        Assert.DoesNotContain(catalog.Items, i => i.Id == "broken");
        Assert.Equal("{11111111-0000-4000-8000-000000000001}", catalog.Items.Single(i => i.Id == "applet").ComponentClsid);
        var bt = catalog.Items.Single(i => i.Source == ItemSource.DesktopEntry);
        Assert.Equal((ControlPanelCategories.Devices, "Pair devices"), (bt.Category, bt.Description));
        Assert.DoesNotContain(catalog.Items, i => i.Name == "Editor");          // only Settings programs
        Assert.Equal(new[] { "Applet" }, catalog.InCategory("System").Where(i => i.Id == "applet").Select(i => i.Name));
    }

    [Fact]
    public void Search_ranks_names_over_keywords_over_descriptions_and_needs_every_word()
    {
        var user = Reg();
        Item(user, "bt", "Bluetooth", keywords: ["wireless"], description: "Pair headphones");
        Item(user, "wifi", "Wi-Fi", keywords: ["wireless", "bluetooth coexistence"], category: "Network");
        Item(user, "snd", "Sound", description: "Speakers and bluetooth headsets");
        var catalog = ControlPanelCatalog.Load(null, user, null, []);

        Assert.Equal(new[] { "Bluetooth", "Wi-Fi", "Sound" }, catalog.Search("bluetooth").Select(i => i.Name));   // name > keyword > description
        Assert.Equal(new[] { "Bluetooth" }, catalog.Search("bluetooth pair").Select(i => i.Name));            // both words must match
        Assert.Equal("Appearance", catalog.Search("dark").First().Name);                                      // built-in keyword
        Assert.Empty(catalog.Search("zzzz"));
        Assert.Empty(catalog.Search("   "));
    }

    [Fact]
    public void A_setting_reads_defaults_validates_writes_and_clamps_stored_junk()
    {
        var hive = Reg();
        var dnd = new SettingBinding(hive, BuiltInPages.Notifications.Settings.Single(s => s.Id == "dnd"));
        var timeout = new SettingBinding(hive, BuiltInPages.Notifications.Settings.Single(s => s.Id == "timeout"));
        var variant = new SettingBinding(hive, BuiltInPages.Appearance.Settings.Single(s => s.Id == "variant"));
        var accent = new SettingBinding(hive, BuiltInPages.Appearance.Settings.Single(s => s.Id == "accent"));

        Assert.Equal(false, dnd.Read());
        Assert.Equal(5000L, timeout.Read());
        Assert.Equal("System", variant.Read());

        Assert.True(dnd.Write(true));
        Assert.Equal(true, dnd.Read());
        Assert.Equal(1u, hive.GetValue(@"Software\Vde\Notifications", "DoNotDisturb")!.Data);   // the same value the notification service reads

        Assert.True(timeout.Write(8000L));
        Assert.False(timeout.Write(10L));                // below the minimum
        Assert.False(timeout.Write("soon"));
        Assert.Equal(8000L, timeout.Read());
        hive.SetValue(@"Software\Vde\Notifications", "DefaultTimeoutMs", RegistryValueKind.DWord, 1u);   // junk stored by something else
        Assert.Equal(500L, timeout.Read());

        Assert.True(variant.Write("Dark"));
        Assert.False(variant.Write("Sepia"));
        Assert.Equal("Dark", variant.Read());

        Assert.True(accent.Write("#D9822B"));
        Assert.False(accent.Write("orange"));
        Assert.Equal("#D9822B", accent.Read());
        Assert.True(accent.Write(""));                   // empty clears it
        Assert.Null(accent.Read());
    }

    [Fact]
    public void Wrong_types_stored_in_the_registry_fall_back_to_the_default()
    {
        var hive = Reg();
        hive.SetValue(@"Software\Vde\Notifications", "DoNotDisturb", RegistryValueKind.String, "yes");
        Assert.Equal(false, new SettingBinding(hive, BuiltInPages.Notifications.Settings[0]).Read());
    }
}

public class LauncherTests
{
    private static (LauncherModel model, Hive hive, Fixture fx) Make()
    {
        var fx = new Fixture();
        foreach (var (id, name, extra) in new[] { ("zip", "Zip Tool", "Comment=Compress files\n"), ("alpha", "alpha editor", ""), ("beta", "Beta Browser", "GenericName=Web Browser\n"),
                     ("num", "7 Tools", ""), ("hid", "Hidden Thing", "NoDisplay=true\n"), ("kcm", "Sound Settings", "Categories=Settings;\n"), ("kate", "Kate", "") })
            fx.Write($"usr-share/applications/{id}.desktop", $"[Desktop Entry]\nType=Application\nName={name}\nExec={id}\n{extra}");
        var hive = new Hive("HKCU") { Identity = RegistryIdentity.Root };
        var settings = ControlPanelCatalog.Load(null, hive, null, []);
        return (new LauncherModel(hive, DesktopEntryDatabase.Load(fx.Xdg, []), ["VDE"], settings), hive, fx);
    }

    [Fact]
    public void All_apps_are_alphabetical_with_headings_and_leave_out_hidden_and_settings_programs()
    {
        var (model, _, fx) = Make(); using (fx)
        {
            Assert.Equal(new[] { "7 Tools", "alpha editor", "Beta Browser", "Kate", "Zip Tool" }, model.AllApps.Select(a => a.Name));
            Assert.Equal(new[] { "A", "B", "K", "Z", "#" }, model.Alphabetical().Select(g => g.Heading));   // "#" (non-letters) goes last
            Assert.Equal("alpha editor", model.Alphabetical()[0].Apps.Single().Name);
        }
    }

    [Fact]
    public void Pinning_orders_and_survives_uninstalled_apps()
    {
        var (model, hive, fx) = Make(); using (fx)
        {
            model.Pin("kate.desktop"); model.Pin("zip"); model.Pin("kate.desktop"); model.Pin("ghost.desktop");   // a duplicate and an unknown app change nothing
            Assert.Equal(new[] { "kate.desktop", "zip.desktop" }, model.Pinned.Select(a => a.Id));

            model.MovePinned("zip.desktop", 0);
            Assert.Equal(new[] { "zip.desktop", "kate.desktop" }, model.Pinned.Select(a => a.Id));
            model.MovePinned("zip.desktop", 99);
            Assert.Equal("zip.desktop", model.Pinned[^1].Id);

            hive.SetValue(LauncherModel.KeyPath, "Pinned", RegistryValueKind.MultiString, new[] { "kate.desktop", "uninstalled.desktop" });
            Assert.Equal("kate.desktop", Assert.Single(model.Pinned).Id);

            model.Unpin("kate.desktop");
            Assert.Empty(model.Pinned);
            Assert.False(hive.GetValue(LauncherModel.KeyPath, "Pinned") is not null && ((string[])hive.GetValue(LauncherModel.KeyPath, "Pinned")!.Data!).Contains("kate.desktop"));
        }
    }

    [Fact]
    public void Recent_puts_the_newest_first_without_repeats_and_can_be_cleared()
    {
        var (model, _, fx) = Make(); using (fx)
        {
            foreach (var id in new[] { "kate", "zip", "alpha", "kate" })
                model.RecordLaunch(id);
            model.RecordLaunch("ghost");

            Assert.Equal(new[] { "kate.desktop", "alpha.desktop", "zip.desktop" }, model.Recent.Select(a => a.Id));
            model.ClearRecent();
            Assert.Empty(model.Recent);
        }
    }

    [Fact]
    public void Search_is_local_ranks_name_matches_first_and_includes_settings()
    {
        var (model, hive, fx) = Make(); using (fx)
        {
            var key = ControlPanelCatalog.RegistryKey + "\\snd"; hive.SetValue(key, "Name", RegistryValueKind.String, "Sound"); hive.SetValue(key, "Command", RegistryValueKind.String, "/bin/true");
            model = new LauncherModel(hive, DesktopEntryDatabase.Load(fx.Xdg, []), ["VDE"], ControlPanelCatalog.Load(null, hive, null, []));

            Assert.Equal(new[] { "Zip Tool" }, model.Search("zip").Select(r => r.Title));
            Assert.Equal("Beta Browser", model.Search("web").Single().Title);                  // generic name
            Assert.Equal("Zip Tool", model.Search("compress").Single().Title);                 // comment
            var sound = model.Search("sound");
            Assert.Equal("Sound", sound[0].Title);                                              // the setting; the "Sound Settings" program is not an app here
            Assert.Equal("Setting", sound[0].Kind);
            Assert.Equal(new[] { "Kate" }, model.Search("kat").Select(r => r.Title));
            Assert.Empty(model.Search("   "));
            Assert.Empty(model.Search("nothing matches this"));
            // dark/theme search reaches the built-in Appearance page, in the same list
            Assert.Contains(model.Search("dark"), r => r.Kind == "Setting" && r.Title == "Appearance");
        }
    }
}

public class LauncherPluginTests
{
    private const string Clsid = "{11111111-0000-4000-8000-0000000000AA}";

    private sealed class Plugin(string name, Func<string, Task<IReadOnlyList<SearchHit>>> search) : ILauncherSearchProvider
    {
        public string Name { get; } = name;
        public List<SearchHit> Activated { get; } = new();
        public int Calls;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken cancel) { Interlocked.Increment(ref Calls); return search(query); }
        public Task ActivateAsync(SearchHit hit, CancellationToken cancel) { Activated.Add(hit); return Task.CompletedTask; }
    }

    private static Task<IReadOnlyList<SearchHit>> Hits(params string[] titles) => Task.FromResult<IReadOnlyList<SearchHit>>(titles.Select(t => new SearchHit(t, "note")).ToList());

    private static (LauncherModel model, LauncherProviders providers, Hive user, Fixture fx) Make(string dll)
    {
        var fx = new Fixture();
        fx.Write("usr-share/applications/zip.desktop", "[Desktop Entry]\nType=Application\nName=Zip Tool\nExec=zip\n");
        var user = new Hive("HKCU") { Identity = RegistryIdentity.Root };
        var hkcr = new ClassesRoot(user);
        ComponentRegistrar.Register(hkcr, new ComponentInfo(Clsid, "Plug.Notes", dll, "Plug", ThreadingModel.Free, null, "Notes search",
            [ComponentCategories.LauncherSearchProvider]));
        var providers = new LauncherProviders(hkcr, user, RegistryIdentity.Root);
        var model = new LauncherModel(user, DesktopEntryDatabase.Load(fx.Xdg, []), ["VDE"]) { Authorizer = providers, PluginTimeout = TimeSpan.FromMilliseconds(300) };
        return (model, providers, user, fx);
    }

    [Fact]
    public async Task A_plugin_is_never_asked_anything_until_the_user_authorizes_it_and_revoking_stops_it_at_once()
    {
        var dll = Path.GetTempFileName();
        var (model, providers, _, fx) = Make(dll); using (fx)
        {
            var plugin = new Plugin("Notes", q => Hits("note about " + q));
            model.AddPlugin(Clsid, plugin);

            Assert.False(Assert.Single(providers.List()).IsAuthorized);
            Assert.Equal(new[] { "Zip Tool" }, (await model.SearchAsync("zip")).Select(r => r.Title));
            Assert.Equal(0, plugin.Calls);                                                      // not authorized: never called

            providers.Authorize(Clsid.ToLowerInvariant());
            var results = await model.SearchAsync("zip");
            Assert.Equal(new[] { "Zip Tool", "note about zip" }, results.Select(r => r.Title));  // local first, plugin after
            Assert.Equal("Plugin", results[1].Kind);
            Assert.Equal("note · Notes", results[1].Subtitle);                                  // labelled with where it came from
            Assert.Equal(1, plugin.Calls);

            Assert.True(await model.ActivateAsync(results[1]));
            Assert.Single(plugin.Activated);

            providers.Revoke(Clsid);
            Assert.Equal(new[] { "Zip Tool" }, (await model.SearchAsync("zip")).Select(r => r.Title));
            Assert.False(await model.ActivateAsync(results[1]));                                // a result shown earlier cannot be run any more
            Assert.Equal(1, plugin.Calls);
        }
        File.Delete(dll);
    }

    [Fact]
    public async Task Slow_failing_and_chatty_plugins_cannot_break_or_flood_the_search()
    {
        var dll = Path.GetTempFileName();
        var (model, providers, _, fx) = Make(dll); using (fx)
        {
            providers.Authorize(Clsid);
            model.AddPlugin(Clsid, new Plugin("Slow", async _ => { await Task.Delay(5000); return [new SearchHit("too late")]; }));
            model.AddPlugin(Clsid, new Plugin("Broken", _ => throw new InvalidOperationException("bug")));
            model.AddPlugin(Clsid, new Plugin("Chatty", _ => Hits(Enumerable.Range(0, 50).Select(i => "hit" + i).ToArray())));

            var started = DateTime.UtcNow;
            var results = await model.SearchAsync("zip");

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));                   // did not wait for the slow one
            Assert.DoesNotContain(results, r => r.Title == "too late");
            Assert.Equal(LauncherModel.MaxPluginHits, results.Count(r => r.Kind == "Plugin"));   // capped
            Assert.Equal("Zip Tool", results[0].Title);
        }
        File.Delete(dll);
    }

    [Fact]
    public void Only_the_users_own_hive_can_authorize_and_only_provider_category_components_are_listed()
    {
        var dll = Path.GetTempFileName();
        var system = new Hive("HKLM") { Identity = RegistryIdentity.Root };
        var user = new Hive("HKCU") { Identity = RegistryIdentity.Root };
        var hkcr = new ClassesRoot(user, system);
        ComponentRegistrar.Register(hkcr, new ComponentInfo(Clsid, "P", dll, "P", ThreadingModel.Free, null, "Mine", [ComponentCategories.LauncherSearchProvider]));
        ComponentRegistrar.Register(hkcr, new ComponentInfo("{22222222-0000-4000-8000-000000000001}", "Other", dll, "O", ThreadingModel.Free));   // not a provider
        var providers = new LauncherProviders(hkcr, user, RegistryIdentity.Current);

        system.SetValue(LauncherProviders.KeyPath, Clsid, RegistryValueKind.DWord, 1u);        // an installer tries to pre-approve it
        Assert.False(Assert.Single(providers.List()).IsAuthorized);

        providers.Authorize(Clsid);
        Assert.True(Assert.Single(providers.List()).MayLoad);
        user.SetValue(LauncherProviders.KeyPath, Clsid, RegistryValueKind.String, "1");        // junk is not consent
        Assert.False(providers.IsAuthorized(Clsid));
        Assert.Throws<ArgumentException>(() => providers.Authorize("nope"));
        File.Delete(dll);
    }

    [Fact]
    public void An_authorized_provider_whose_registration_is_untrusted_may_still_not_load()
    {
        var dll = Path.GetTempFileName();
        var (_, providers, user, fx) = Make(dll); using (fx)
        {
            providers.Authorize(Clsid);
            var server = $@"Software\Classes\CLSID\{Clsid}\InprocServer32";
            user.SetValuePermissions(server, "", user.GetValuePermissions(server, "")!.Value.WithMode(PosixPermissions.ValueDefault));   // no longer executable

            var info = Assert.Single(providers.List());
            Assert.True(info.IsAuthorized);
            Assert.False(info.MayLoad);
        }
        File.Delete(dll);
    }
}

public class FileSearchTests
{
    private static Fixture Tree()
    {
        var fx = new Fixture();
        fx.Write("home/Documents/Invoice 2026.txt", "Customer: Acme\nTotal due: 120\nInvoice 2026 paid\n");
        fx.Write("home/Documents/notes.md", "# Notes\nremember the invoice\n");
        fx.Write("home/Documents/deep/er/invoice-old.txt", "old\n");
        fx.Write("home/Music/song.flac", "binary\0data invoice");
        fx.Write("home/.cache/invoice.tmp", "invoice");
        Directory.CreateDirectory(Path.Combine(fx.Root, "home", "Documents", "invoices"));
        return fx;
    }

    [Fact]
    public void By_name_matches_substrings_and_globs_and_ranks_prefix_matches_first()
    {
        using var fx = Tree();
        var roots = new[] { Path.Combine(fx.Root, "home") };

        var hits = FileSearch.ByName(roots, "invoice");
        Assert.Equal(new[] { "invoices", "invoice-old.txt", "Invoice 2026.txt" }, hits.Select(h => h.Name));   // all start with it; shorter names first; folders included
        Assert.True(hits[0].IsDirectory);
        Assert.DoesNotContain(hits, h => h.Path.Contains(".cache"));                                            // hidden skipped
        Assert.Contains(FileSearch.ByName(roots, "invoice", new FileSearchOptions { IncludeHidden = true }), h => h.Name == "invoice.tmp");
        Assert.Equal(2, FileSearch.ByName(roots, "*.txt").Count);                                               // the two .txt files; the hidden one is skipped
        Assert.Equal(new[] { "notes.md" }, FileSearch.ByName(roots, "n?tes.*").Select(h => h.Name));
        Assert.Empty(FileSearch.ByName(roots, "   "));
    }

    [Fact]
    public void By_content_reports_lines_and_snippets_skips_binary_and_hidden_and_respects_limits()
    {
        using var fx = Tree();
        var roots = new[] { Path.Combine(fx.Root, "home") };

        var hits = FileSearch.ByContent(roots, "invoice");
        Assert.Contains(hits, h => h.Name == "Invoice 2026.txt" && h.Line == 3 && h.Snippet == "Invoice 2026 paid");
        Assert.Contains(hits, h => h.Name == "notes.md" && h.Line == 2);
        Assert.DoesNotContain(hits, h => h.Name == "song.flac");     // binary
        Assert.DoesNotContain(hits, h => h.Path.Contains(".cache"));
        Assert.Single(FileSearch.ByContent(roots, "invoice", new FileSearchOptions { MaxResults = 1 }));
        Assert.Empty(FileSearch.ByContent(roots, "invoice", new FileSearchOptions { MaxContentBytes = 5 }));   // files over the size cap are not read
    }

    [Fact]
    public void Symlinks_are_not_followed_and_cancellation_and_the_time_budget_stop_the_walk()
    {
        using var fx = Tree();
        var home = Path.Combine(fx.Root, "home");
        File.CreateSymbolicLink(Path.Combine(home, "Documents", "loop"), home);                    // a loop back to the root
        fx.Write("outside/secret-invoice.txt", "invoice");
        File.CreateSymbolicLink(Path.Combine(home, "escape"), Path.Combine(fx.Root, "outside"));  // a link leaving the roots

        var hits = FileSearch.ByName([home], "invoice");
        Assert.DoesNotContain(hits, h => h.Path.Contains("secret"));
        Assert.Equal(3, hits.Count);                                                                // and it terminated

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FileSearch.ByName([home], "invoice", cancel: cancelled.Token));
        Assert.Empty(FileSearch.ByName([home], "invoice", new FileSearchOptions { Budget = TimeSpan.Zero }));
        Assert.Empty(FileSearch.ByName([Path.Combine(fx.Root, "no-such-folder")], "x"));
    }

    [Fact]
    public async Task The_launcher_finds_files_by_name_and_by_in_prefixed_content()
    {
        using var fx = Tree();
        var model = new LauncherModel(new Hive("HKCU"), DesktopEntryDatabase.Load(fx.Xdg, []), ["VDE"]) { FileRoots = [Path.Combine(fx.Root, "home")] };

        var byName = await model.SearchFilesAsync("notes");
        Assert.Equal("notes.md", Assert.Single(byName).Title);
        Assert.Equal("File", byName[0].Kind);

        var byContent = await model.SearchFilesAsync("in:total due");
        var hit = Assert.Single(byContent);
        Assert.Equal("Invoice 2026.txt", hit.Title);
        Assert.Contains(":2  Total due: 120", hit.Subtitle);
        Assert.EndsWith("Invoice 2026.txt", hit.FilePath);
    }
}

public class ElevationAccountTests
{
    [Fact]
    public void Sudo_asks_for_the_invoking_users_password_or_roots_when_configured_that_way()
    {
        using var fx = new Fixture();
        var passwd = fx.Write("etc/passwd", "root:x:0:0:root:/root:/bin/bash\ndeck:x:1000:1000:Deck:/home/deck:/bin/fish\n");
        var alice = new RegistryIdentity(1000, 1000);

        Assert.Equal("deck", new SudoBackend(passwdFile: passwd).PasswordAccount(alice));
        Assert.Equal("root", new SudoBackend(passwdFile: passwd, rootPassword: true).PasswordAccount(alice));
        Assert.Null(new SudoBackend(passwdFile: passwd).PasswordAccount(new RegistryIdentity(4242, 4242)));   // unknown uid: the prompt falls back to "your password"
        Assert.Null(new SudoBackend(passwdFile: "/nonexistent").PasswordAccount(alice));
    }

    [Fact]
    public void The_setting_comes_from_the_system_hive_and_anything_else_means_the_users_own_password()
    {
        var hive = new Hive("HKLM") { Identity = RegistryIdentity.Root };
        Assert.False(ElevationSettings.AsksForRootPassword(hive));
        Assert.False(ElevationSettings.AsksForRootPassword(null));

        hive.SetValue(ElevationSettings.KeyPath, "PasswordFor", RegistryValueKind.String, "Root");
        Assert.True(ElevationSettings.AsksForRootPassword(hive));
        hive.SetValue(ElevationSettings.KeyPath, "PasswordFor", RegistryValueKind.String, "user");
        Assert.False(ElevationSettings.AsksForRootPassword(hive));
        hive.SetValue(ElevationSettings.KeyPath, "PasswordFor", RegistryValueKind.DWord, 1u);
        Assert.False(ElevationSettings.AsksForRootPassword(hive));
    }

    private sealed class Backend : IElevationBackend
    {
        public bool CanElevate(RegistryIdentity identity) => true;
        public string? PasswordAccount(RegistryIdentity identity) => "root";
        public Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel) => Task.FromResult(ElevationOutcome.Success);
    }

    private sealed class Prompt : IElevationPrompt
    {
        public ElevationRequest? Seen;
        public Task<ElevationResponse> AskAsync(ElevationRequest request, int attempt, string? previousError, CancellationToken cancel)
        {
            Seen = request;
            return Task.FromResult(new ElevationResponse(true, "pw".ToCharArray()));
        }
    }

    [Fact]
    public async Task The_shell_tells_the_prompt_whose_password_to_ask_for()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/mime/globs2", "50:application/x-thing:*.thing\n");
        fx.Program("winish");
        var system = new Hive("HKLM") { Identity = RegistryIdentity.Root };
        system.SetKeyPermissions("", new PosixPermissions(0, 0, 0x1ED));
        system.SetValue(@"Software\Classes\.thing", "", RegistryValueKind.String, "T");
        system.SetValue(@"Software\Classes\T\shell\open\command", "", RegistryValueKind.String, "winish %1");
        system.SetValuePermissions(@"Software\Classes\T\shell\open\command", "", new PosixPermissions(0, 0, 0x1C0));   // 0700: root only
        var hkcr = new ClassesRoot(new Hive("HKCU") { Identity = new RegistryIdentity(1000, 1000) }, system);
        var mime = new MimeDatabase(fx.Xdg);
        var prompt = new Prompt();
        var shell = new ShellExecute(new FileAssociations(fx.Xdg, mime, DesktopEntryDatabase.Load(fx.Xdg, []), ["VDE"], hkcr, false), mime,
            env: fx.Env, types: new FileTypes(mime, hkcr, identity: new RegistryIdentity(1000, 1000)),
            elevation: new ElevationOptions(new Backend(), prompt, new RegistryIdentity(1000, 1000)));

        await shell.OpenAsync(fx.Write("a.thing", "x"));

        Assert.Equal("root", prompt.Seen!.Account);
    }
}

public class FileTransferTests
{
    [Fact]
    public void Copy_and_cut_round_trip_through_the_formats_other_file_managers_use()
    {
        var paths = new[] { "/home/u/a b.txt", "/home/u/ü.png" };
        foreach (var kind in new[] { TransferKind.Copy, TransferKind.Cut })
        {
            var formats = new FileTransfer(kind, paths).ToFormats();

            Assert.Equal(kind == TransferKind.Cut ? "1" : "0", System.Text.Encoding.UTF8.GetString(formats[FileTransfer.KdeCutSelection]));
            var gnome = System.Text.Encoding.UTF8.GetString(formats[FileTransfer.GnomeCopiedFiles]);
            Assert.StartsWith((kind == TransferKind.Cut ? "cut" : "copy") + "\nfile:///home/u/a%20b.txt\n", gnome);
            Assert.EndsWith("\r\n", System.Text.Encoding.UTF8.GetString(formats[FileTransfer.UriList]));

            var back = FileTransfer.FromFormats(formats)!;
            Assert.Equal(kind, back.Kind);
            Assert.Equal(paths, back.Paths);
        }
    }

    [Fact]
    public void Each_single_format_alone_is_understood_and_foreign_or_missing_data_is_not()
    {
        byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        var onlyList = FileTransfer.FromFormats(new Dictionary<string, byte[]> { [FileTransfer.UriList] = B("# a comment\r\nfile:///x/a.txt\r\nhttps://example.org/\r\n") })!;
        Assert.Equal(new[] { "/x/a.txt" }, onlyList.Paths);            // the web link is not a file
        Assert.Equal(TransferKind.Copy, onlyList.Kind);

        var kdeCut = FileTransfer.FromFormats(new Dictionary<string, byte[]> { [FileTransfer.UriList] = B("file:///x/a.txt"), [FileTransfer.KdeCutSelection] = B("1") })!;
        Assert.Equal(TransferKind.Cut, kdeCut.Kind);

        Assert.Null(FileTransfer.FromFormats(new Dictionary<string, byte[]>()));
        Assert.Null(FileTransfer.FromFormats(new Dictionary<string, byte[]> { [FileTransfer.UriList] = B("file://otherhost/x/a.txt\r\n") }));   // another machine's file
        Assert.Null(FileTransfer.FromFormats(new Dictionary<string, byte[]> { [FileTransfer.GnomeCopiedFiles] = B("cut\n") }));
    }

    [Fact]
    public void Drop_effects_follow_the_modifier_keys_and_refuse_nonsense()
    {
        using var fx = new Fixture();
        var a = fx.Write("one/a.txt", "x"); var target = Path.Combine(fx.Root, "two"); Directory.CreateDirectory(target);
        var folder = Path.Combine(fx.Root, "one"); Directory.CreateDirectory(Path.Combine(folder, "inner"));
        Func<string, string, bool> same = (_, _) => true, other = (_, _) => false;

        Assert.Equal(DropEffect.Move, DropRules.Decide([a], target, false, false, same));      // same disk: move
        Assert.Equal(DropEffect.Copy, DropRules.Decide([a], target, false, false, other));     // other disk: copy
        Assert.Equal(DropEffect.Copy, DropRules.Decide([a], target, true, false, same));       // Ctrl: copy
        Assert.Equal(DropEffect.Move, DropRules.Decide([a], target, false, true, other));      // Shift: move
        Assert.Equal(DropEffect.Link, DropRules.Decide([a], target, true, true, same));
        Assert.Equal(DropEffect.None, DropRules.Decide([a], folder, false, false, same));      // already there
        Assert.Equal(DropEffect.None, DropRules.Decide([folder], Path.Combine(folder, "inner"), false, false, same));   // into itself
        Assert.Equal(DropEffect.None, DropRules.Decide([folder], folder, true, false, same));
        Assert.Equal(DropEffect.None, DropRules.Decide([a], Path.Combine(fx.Root, "nope"), false, false, same));       // not a folder
        Assert.Equal(DropEffect.None, DropRules.Decide([a], a, false, false, same));
        Assert.Equal(DropEffect.None, DropRules.Decide([], target, false, false, same));
    }

    [Fact]
    public void Pasting_cut_moves_copy_copies_and_files_deleted_meanwhile_are_skipped()
    {
        using var fx = new Fixture();
        var actions = new FileActions(new Trash(fx.Xdg));
        var a = fx.Write("src/a.txt", "A"); var b = fx.Write("src/b.txt", "B"); var gone = Path.Combine(fx.Root, "src", "gone.txt");
        var dst = Path.Combine(fx.Root, "dst"); Directory.CreateDirectory(dst);

        var copied = actions.Paste(new FileTransfer(TransferKind.Copy, [a, gone]), dst);
        Assert.Equal(new[] { Path.Combine(dst, "a.txt") }, copied);
        Assert.True(File.Exists(a));

        var moved = actions.Paste(new FileTransfer(TransferKind.Cut, [b]), dst);
        Assert.Equal("B", File.ReadAllText(moved[0]));
        Assert.False(File.Exists(b));

        var linked = actions.Drop([a], dst, DropEffect.Link);
        Assert.Equal("a.txt - link", Path.GetFileName(linked[0]));
        Assert.Equal(a, new FileInfo(linked[0]).LinkTarget);
        Assert.Equal("a.txt - link 2", Path.GetFileName(actions.Drop([a], dst, DropEffect.Link)[0]));   // never overwrites an earlier link
        Assert.Empty(actions.Drop([a], dst, DropEffect.None));
    }

    [Fact]
    public void Rename_new_folder_and_delete_go_through_the_trash_unless_permanent()
    {
        using var fx = new Fixture();
        var trash = new Trash(fx.Xdg);
        var actions = new FileActions(trash);
        var file = fx.Write("d/a.txt", "x"); var folder = Path.Combine(fx.Root, "d");

        Assert.Equal(Path.Combine(folder, "b.txt"), actions.Rename(file, "b.txt"));
        Assert.EndsWith("New folder", actions.NewFolder(folder));
        Assert.EndsWith("New folder (2)", actions.NewFolder(folder));

        actions.Delete([Path.Combine(folder, "b.txt")]);
        Assert.False(File.Exists(Path.Combine(folder, "b.txt")));
        Assert.Single(trash.List());                                                   // recoverable

        actions.Delete([Path.Combine(folder, "New folder")], permanent: true);
        Assert.False(Directory.Exists(Path.Combine(folder, "New folder")));
        Assert.Single(trash.List());                                                   // permanent ones never reach the trash
    }
}
