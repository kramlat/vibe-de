using Vde.Freedesktop;

namespace Vde.Freedesktop.Tests;

// Builds a throwaway XDG tree so tests never depend on the machine's installed themes.
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-fd-" + Guid.NewGuid().ToString("N"));
    public string Home => Path.Combine(Root, "home");
    public string System => Path.Combine(Root, "usr-share");
    public XdgDirectories Xdg { get; }

    public Fixture()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(System);
        var env = new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["XDG_DATA_HOME"] = Path.Combine(Home, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, "config"),
            ["XDG_DATA_DIRS"] = System,
        };
        Xdg = new XdgDirectories(k => env.GetValueOrDefault(k));
    }

    public string Write(string relativeToRoot, string content = "")
    {
        var path = Path.Combine(Root, relativeToRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class XdgTests
{
    [Fact]
    public void Defaults_and_overrides()
    {
        var empty = new XdgDirectories(k => k == "HOME" ? "/home/u" : null);
        Assert.Equal("/home/u/.local/share", empty.DataHome);
        Assert.Equal("/home/u/.config", empty.ConfigHome);
        Assert.Equal(new[] { "/usr/local/share", "/usr/share" }, empty.DataDirs);
        Assert.Equal("/home/u/.local/share", empty.DataSearchPath[0]);

        var env = new Dictionary<string, string>
        {
            ["HOME"] = "/home/u", ["XDG_DATA_HOME"] = "relative/ignored", ["XDG_DATA_DIRS"] = "/a:rel:/b",
        };
        var set = new XdgDirectories(k => env.GetValueOrDefault(k));
        Assert.Equal("/home/u/.local/share", set.DataHome);   // relative path ignored
        Assert.Equal(new[] { "/a", "/b" }, set.DataDirs);
    }

    [Fact]
    public void User_dirs_come_from_user_dirs_dirs_with_defaults()
    {
        using var fx = new Fixture();
        fx.Write("home/config/user-dirs.dirs", "# c\nXDG_DOWNLOAD_DIR=\"$HOME/Stuff\"\nXDG_MUSIC_DIR=\"/mnt/music\"\n");

        Assert.Equal(Path.Combine(fx.Home, "Stuff"), fx.Xdg.UserDir("DOWNLOAD"));
        Assert.Equal("/mnt/music", fx.Xdg.UserDir("MUSIC"));
        Assert.Equal(Path.Combine(fx.Home, "Documents"), fx.Xdg.UserDir("DOCUMENTS"));
    }
}

public class IniAndLocaleTests
{
    [Theory]
    [InlineData("de_AT.UTF-8@euro", new[] { "de_AT@euro", "de_AT", "de@euro", "de" })]
    [InlineData("fr_CA", new[] { "fr_CA", "fr" })]
    [InlineData("en", new[] { "en" })]
    [InlineData("C", new string[0])]
    [InlineData(null, new string[0])]
    public void Locale_candidates(string? locale, string[] expected)
        => Assert.Equal(expected, Locale.Candidates(locale));

    [Fact]
    public void Ini_parsing_escapes_lists_and_first_key_wins()
    {
        var ini = IniFile.Parse("junk=1\n# c\n[G]\nA = x\\sy\\\\z\nA=ignored\nL=a;b\\;c;d;\nB=true\nName=Hi\nName[de]=Hallo\n");

        Assert.Null(ini.Get("", "junk"));
        Assert.Equal("x y\\z", ini.Get("G", "A"));
        Assert.Equal(new[] { "a", "b;c", "d" }, ini.GetList("G", "L"));
        Assert.True(ini.GetBool("G", "B"));
        Assert.Equal("Hallo", ini.GetLocalized("G", "Name", ["de_AT", "de"]));
        Assert.Equal("Hi", ini.GetLocalized("G", "Name", ["fr"]));
    }
}

public class DesktopEntryTests
{
    private const string Sample = """
        [Desktop Entry]
        Type=Application
        Name=Files
        Name[de]=Dateien
        Exec=files --new-window %U
        Icon=system-file-manager
        Categories=System;FileManager;
        MimeType=inode/directory;
        OnlyShowIn=KDE;VDE;
        Actions=new;

        [Desktop Action new]
        Name=New Window
        Exec=files --new-window
        """;

    [Fact]
    public void Parses_fields_actions_and_locale()
    {
        var e = DesktopEntry.Parse(Sample, "files.desktop", ["de"]);

        Assert.Equal("Dateien", e.Name);
        Assert.Equal(new[] { "System", "FileManager" }, e.Categories);
        Assert.Equal("New Window", e.Actions.Single().Name);
        Assert.True(e.IsListed(["VDE"]));
        Assert.False(e.IsListed(["GNOME"]));
    }

    [Fact]
    public void Missing_group_is_an_error()
        => Assert.Throws<InvalidDataException>(() => DesktopEntry.Parse("[Other]\nName=x", "x.desktop", []));

    [Theory]
    [InlineData("app %F", new[] { "/a", "/b" }, new[] { "app", "/a", "/b" })]
    [InlineData("app %U", new[] { "/a b" }, new[] { "app", "file:///a%20b" })]
    [InlineData("app %f", new[] { "file:///x/y%20z" }, new[] { "app", "/x/y z" })]
    [InlineData("app %f", new string[0], new[] { "app" })]
    [InlineData("app --title=%c %%", new string[0], new[] { "app", "--title=Files", "%" })]
    [InlineData("\"my app\" \"a \\\"q\\\" b\"", new string[0], new[] { "my app", "a \"q\" b" })]
    [InlineData("app %i %k", new string[0], new[] { "app", "--icon", "folder", "/f.desktop" })]
    [InlineData("app %d %n", new string[0], new[] { "app" })]
    public void Exec_expansion(string exec, string[] targets, string[] expected)
        => Assert.Equal(expected, ExecLine.Expand(exec, targets, "Files", "folder", "/f.desktop"));

    [Fact]
    public void Database_prefers_user_entries_ids_use_dashes_and_hidden_masks()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/applications/a.desktop", "[Desktop Entry]\nType=Application\nName=System A\n");
        fx.Write("home/data/applications/a.desktop", "[Desktop Entry]\nType=Application\nName=User A\n");
        fx.Write("usr-share/applications/sub/b.desktop", "[Desktop Entry]\nType=Application\nName=B\n");
        fx.Write("usr-share/applications/gone.desktop", "[Desktop Entry]\nType=Application\nName=Gone\n");
        fx.Write("home/data/applications/gone.desktop", "[Desktop Entry]\nHidden=true\n");
        fx.Write("usr-share/applications/bad.desktop", "not a desktop file");

        var db = DesktopEntryDatabase.Load(fx.Xdg, []);

        Assert.Equal("User A", db.Find("a")!.Name);
        Assert.Equal("B", db.Find("sub-b.desktop")!.Name);
        Assert.Null(db.Find("gone"));
        Assert.Single(db.Errors);
    }
}

public class ThemeLookupTests
{
    private const string BreezeIndex = """
        [Icon Theme]
        Name=T
        Inherits=base
        Directories=16,32,scalable

        [16]
        Size=16
        Type=Fixed

        [32]
        Size=32
        Type=Fixed

        [scalable]
        Size=64
        MinSize=48
        MaxSize=256
        Type=Scalable
        """;

    private static Fixture IconFixture()
    {
        var fx = new Fixture();
        fx.Write("usr-share/icons/t/index.theme", BreezeIndex);
        fx.Write("usr-share/icons/t/16/folder.png");
        fx.Write("usr-share/icons/t/32/folder.png");
        fx.Write("usr-share/icons/t/scalable/folder.svg");
        fx.Write("usr-share/icons/t/16/net-wifi.png");
        fx.Write("usr-share/icons/t/16/net.png");
        fx.Write("usr-share/icons/base/index.theme", "[Icon Theme]\nName=base\nDirectories=16\n\n[16]\nSize=16\nType=Fixed\n");
        fx.Write("usr-share/icons/base/16/inherited.png");
        fx.Write("usr-share/icons/hicolor/index.theme", "[Icon Theme]\nName=hicolor\nDirectories=16\n\n[16]\nSize=16\nType=Fixed\n");
        fx.Write("usr-share/icons/hicolor/16/fallback.png");
        fx.Write("usr-share/pixmaps/legacy.xpm");
        return fx;
    }

    [Theory]
    [InlineData(16, 1, "16/folder.png")]
    [InlineData(32, 1, "32/folder.png")]
    [InlineData(20, 1, "16/folder.png")]       // 20 is nearer 16 than 32
    [InlineData(28, 1, "32/folder.png")]
    [InlineData(128, 1, "scalable/folder.svg")] // inside the scalable range
    [InlineData(16, 2, "32/folder.png")]       // 16@2x is 32 device pixels
    public void Picks_the_closest_directory(int size, int scale, string expectedTail)
    {
        using var fx = IconFixture();
        var found = new IconThemeLookup(fx.Xdg).Find("folder", size, scale, "t");

        Assert.NotNull(found);
        Assert.EndsWith(expectedTail.Replace('/', Path.DirectorySeparatorChar), found.Path);
    }

    [Fact]
    public void Inherits_falls_back_to_hicolor_shortens_names_and_reads_pixmaps()
    {
        using var fx = IconFixture();
        var lookup = new IconThemeLookup(fx.Xdg);

        Assert.EndsWith("base/16/inherited.png".Replace('/', Path.DirectorySeparatorChar), lookup.Find("inherited", 16, 1, "t")!.Path);
        Assert.EndsWith("hicolor/16/fallback.png".Replace('/', Path.DirectorySeparatorChar), lookup.Find("fallback", 16, 1, "t")!.Path);
        Assert.EndsWith("net.png", lookup.Find("net-missing-suffix", 16, 1, "t")!.Path);   // shortened to "net"
        Assert.EndsWith("net-wifi.png", lookup.Find("net-wifi-strong", 16, 1, "t")!.Path);  // shortened to "net-wifi"
        Assert.EndsWith("legacy.xpm", lookup.Find("legacy", 16, 1, "t")!.Path);
        Assert.Null(lookup.Find("nothing-here", 16, 1, "t"));
    }

    [Fact]
    public void Absolute_icon_paths_are_used_directly()
    {
        using var fx = IconFixture();
        var path = fx.Write("custom.png");
        Assert.Equal(path, new IconThemeLookup(fx.Xdg).Find(path, 16)!.Path);
        Assert.Null(new IconThemeLookup(fx.Xdg).Find(path + "x", 16));
    }

    [Fact]
    public void Inheritance_cycles_terminate()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/icons/a/index.theme", "[Icon Theme]\nInherits=b\nDirectories=\n");
        fx.Write("usr-share/icons/b/index.theme", "[Icon Theme]\nInherits=a\nDirectories=\n");
        Assert.Null(new IconThemeLookup(fx.Xdg).Find("x", 16, 1, "a"));
    }

    [Fact]
    public void Sound_lookup_follows_inheritance_and_disabled_markers()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/sounds/freedesktop/index.theme", "[Sound Theme]\nDirectories=stereo\n");
        var bell = fx.Write("usr-share/sounds/freedesktop/stereo/bell.oga");
        fx.Write("usr-share/sounds/freedesktop/stereo/quiet.oga");
        fx.Write("usr-share/sounds/mine/index.theme", "[Sound Theme]\nInherits=freedesktop\nDirectories=stereo\n");
        fx.Write("usr-share/sounds/mine/stereo/quiet.disabled");

        var sounds = new SoundThemeLookup(fx.Xdg);

        Assert.Equal(bell, sounds.Find("bell", "mine"));
        Assert.Null(sounds.Find("quiet", "mine"));          // mine disables it
        Assert.NotNull(sounds.Find("quiet", "freedesktop"));
        Assert.Null(sounds.Find("missing", "mine"));
    }

    [Fact]
    public void Cursor_lookup_follows_inherits()
    {
        using var fx = new Fixture();
        fx.Write("usr-share/icons/child/index.theme", "[Icon Theme]\nInherits=parent\n");
        var arrow = fx.Write("usr-share/icons/parent/cursors/left_ptr");

        Assert.Equal(arrow, new CursorThemeLookup(fx.Xdg).Find("left_ptr", "child"));
        Assert.Null(new CursorThemeLookup(fx.Xdg).Find("nope", "child"));
    }
}
