using Vde.Components;
using Vde.Shell;

namespace Vde.Shell.Tests;

public class ShellVfsTests
{
    [Fact]
    public void Opening_an_item_goes_into_a_child_folder_or_directory_opens_a_file_or_runs_its_action()
    {
        var dir = Path.GetTempPath();
        var file = Path.GetTempFileName();
        try
        {
            ShellItem Item(Func<ShellItem, ShellItem> f) => f(new ShellItem("i", "Item", true, []));
            Assert.Equal(new VfsOpen(VfsOpenKind.Enter, "shell:Network"), ShellVfs.Open(Item(i => i with { ChildFolder = "shell:Network" })));
            Assert.Equal(new VfsOpen(VfsOpenKind.Enter, dir), ShellVfs.Open(Item(i => i with { FileSystemPath = dir })));
            Assert.Equal(new VfsOpen(VfsOpenKind.OpenFile, file), ShellVfs.Open(Item(i => i with { FileSystemPath = file })));
            Assert.Equal(new VfsOpen(VfsOpenKind.RunAction, Action: "open"), ShellVfs.Open(Item(i => i with { DefaultAction = "open" })));
            Assert.Equal(VfsOpenKind.None, ShellVfs.Open(Item(i => i)).Kind);
        }
        finally { File.Delete(file); }
    }

    private sealed class Drives(string path) : IShellFolder
    {
        public string DisplayName => "This PC";
        public IReadOnlyList<string> IconNames => [];
        public IReadOnlyList<ShellItem> GetItems() => [new ShellItem("d", "Data", true, []) { FileSystemPath = path }];
        public IReadOnlyList<ShellAction> GetActions(ShellItem? item) => [];
        public Task InvokeAsync(string actionId, ShellItem? item, CancellationToken cancel) => Task.CompletedTask;
        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void Resolve_turns_shell_locations_into_paths_for_every_file_consumer_and_files_operate_through_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "vfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new ShellFolderCatalog(new Vibe.Registry.ClassesRoot(new Vibe.Registry.Hive("HKCU"), new Vibe.Registry.Hive("HKLM")), null, null, new ComponentLoader());
        catalog.AddBuiltIn("{22222222-2222-3333-4444-555555555555}", "Computer", "This PC", ShellFolderAttributes.Folder, new Drives(root), ["Sidebar"]);
        var saved = ShellVfs.Default;
        ShellVfs.Default = new ShellVfs(catalog);
        try
        {
            Assert.Equal(root, ShellVfs.Resolve("shell:Computer/d"));
            Assert.Equal("/plain/path", ShellVfs.Resolve("/plain/path"));
            Assert.Throws<IOException>(() => ShellVfs.Resolve("shell:Computer"));

            var made = FileOperations.CreateFolder("shell:Computer/d", "Made");
            Assert.Equal(Path.Combine(root, "Made"), made);
            Assert.Equal(Path.Combine(root, "Renamed"), FileOperations.Rename("shell:Computer/d/Made".Replace("shell:Computer/d", root), "Renamed"));
        }
        finally { ShellVfs.Default = saved; Directory.Delete(root, recursive: true); }
    }
}

public class StartFolderTests
{
    [Fact]
    public void Start_shows_Settings_File_Explorer_Documents_and_Downloads_until_the_user_chooses_others()
    {
        var hive = new Vibe.Registry.Hive("HKCU");
        Assert.Equal(new[] { "Settings", "Files", "Documents", "Downloads" }, StartFolders.Visible(hive).Select(f => f.Id));

        StartFolders.Set(hive, "Music", true);
        StartFolders.Set(hive, "Home", true);
        StartFolders.Set(hive, "Documents", false);
        Assert.Equal(new[] { "Settings", "Files", "Downloads", "Music", "Home" }, StartFolders.Visible(hive).Select(f => f.Id));
        Assert.Equal("Home folder", StartFolders.All.Single(f => f.Id == "Home").Label);
        Assert.Equal("shell:Home", StartFolders.Location("Home"));
        Assert.Null(StartFolders.Location("Settings"));
    }
}

public class StartMenuPlacementTests
{
    private static readonly PixelBox Screen = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(TaskbarEdge.Bottom, TaskbarAlignment.Center, 668, 460)]   // centred, just above the bar
    [InlineData(TaskbarEdge.Bottom, TaskbarAlignment.Start, 12, 460)]     // at the left, above the bar
    [InlineData(TaskbarEdge.Top, TaskbarAlignment.Center, 668, 60)]       // below the bar
    [InlineData(TaskbarEdge.Left, TaskbarAlignment.Start, 60, 12)]        // beside the bar, at its top
    [InlineData(TaskbarEdge.Right, TaskbarAlignment.Start, 1276, 12)]     // beside the bar on the right
    public void The_start_menu_opens_against_the_taskbar_on_the_side_of_the_start_button(TaskbarEdge edge, TaskbarAlignment alignment, int x, int y)
        => Assert.Equal((x, y), StartMenuPlacement.Compute(Screen, edge, alignment, 48, 584, 560));

    [Fact]
    public void A_menu_taller_than_the_room_is_kept_on_the_screen()
    {
        var (x, y) = StartMenuPlacement.Compute(new PixelBox(0, 0, 800, 500), TaskbarEdge.Bottom, TaskbarAlignment.Center, 48, 584, 620);
        Assert.Equal(0, y);
        Assert.InRange(x, 0, 800 - 584);
    }
}

public class FontTests
{
    private const string Listing = "Arial\tRegular\t/f/arial.ttf\ten|de\nArial\tBold\t/f/arialbd.ttf\ten|de\nAGA Arabesque\tRegular\t/f/aga.ttf\tar\n";

    [Fact]
    public void Fonts_are_grouped_by_family_with_their_face_count_and_languages()
    {
        var fonts = FontCatalog.Parse(Listing);
        Assert.Equal(new[] { "AGA Arabesque", "Arial" }, fonts.Select(f => f.Name));
        Assert.Equal("2 font faces", fonts[1].FacesText);
        Assert.Equal("1 font face", fonts[0].FacesText);
        Assert.Contains("de", fonts[1].Languages);
        Assert.Equal("English", FontCatalog.LanguageName("en"));
    }

    [Fact]
    public void Installing_copies_only_font_files_and_the_system_command_runs_as_one_install()
    {
        var root = Path.Combine(Path.GetTempPath(), "fonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var font = Path.Combine(root, "a.ttf"); var other = Path.Combine(root, "notes.txt");
            File.WriteAllText(font, "x"); File.WriteAllText(other, "x");
            var result = FontInstaller.InstallInto([font, other], Path.Combine(root, "dest"));
            Assert.Equal(new[] { Path.Combine(root, "dest", "a.ttf") }, result.Installed);
            Assert.Equal(new[] { other }, result.Skipped);
            Assert.True(File.Exists(Path.Combine(root, "dest", "a.ttf")));

            var command = FontInstaller.SystemCommand([font], "/usr/local/share/fonts");
            Assert.Equal("sh", command[0]);
            Assert.Equal(new[] { "/usr/local/share/fonts", font }, command.Skip(4));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Install_locally_is_on_by_default_and_remembered()
    {
        var hive = new Vibe.Registry.Hive("HKCU");
        Assert.True(FontInstaller.InstallLocally(hive));
        FontInstaller.SetInstallLocally(hive, false);
        Assert.False(FontInstaller.InstallLocally(hive));
    }
}

public class TrayPreferenceTests
{
    [Fact]
    public void The_taskbar_remembers_tray_icons_and_the_user_moves_them_to_the_overflow()
    {
        var hive = new Vibe.Registry.Hive("HKCU");
        var key = TrayPreferences.KeyFor("org.kde.StatusNotifierItem-1234-1");
        TrayPreferences.Remember(hive, key, "Steam", "steam_tray_mono");
        TrayPreferences.Remember(hive, TrayPreferences.KeyFor("nm-applet"), "Network", "network-wired");

        Assert.All(TrayPreferences.Known(hive), e => Assert.True(e.InCorner));   // every icon starts in the corner
        TrayPreferences.Set(hive, key, false);

        var steam = TrayPreferences.Known(hive).Single(e => e.Title == "Steam");
        Assert.False(steam.InCorner);
        Assert.Equal("steam_tray_mono", steam.IconName);
        Assert.Equal(new[] { "Network", "Steam" }, TrayPreferences.Definitions(hive).Select(d => d.Label));
    }

    [Fact]
    public void Show_desktop_hides_every_window_and_brings_back_the_same_ones()
    {
        var windows = new FakeWindows([new("1", "a", "a", IsActive: true), new("2", "b", "b"), new("3", "c", "c", IsMinimized: true)]);
        using var model = new TaskbarModel(new Vibe.Registry.Hive("HKCU"), Vde.Freedesktop.DesktopEntryDatabase.Load(new Vde.Freedesktop.XdgDirectories(k => k switch { "HOME" => "/nonexistent", "XDG_DATA_HOME" => "/nonexistent", "XDG_DATA_DIRS" => "/nonexistent", _ => null }), []), windows, _ => { });

        Assert.True(model.ToggleShowDesktop());
        Assert.Equal(new[] { "min 1", "min 2" }, windows.Log);   // the already-minimized one is left alone
        windows.MinimizeAll();
        Assert.False(model.ToggleShowDesktop());
        Assert.Equal(new[] { "min 1", "min 2", "act 1", "act 2" }, windows.Log);
    }

    private sealed class FakeWindows(List<WindowInfo> list) : IWindowSource
    {
        public List<string> Log { get; } = [];
        public IReadOnlyList<WindowInfo> Windows => list;
        public event EventHandler? Changed { add { } remove { } }
        public void Activate(string id) => Log.Add("act " + id);
        public void Minimize(string id) { Log.Add("min " + id); }
        public void Close(string id) { }
        public void MinimizeAll() { for (var i = 0; i < list.Count; i++) list[i] = list[i] with { IsMinimized = true }; }
    }
}

public class WidgetTests
{
    private const string Geocode = "{\"results\":[{\"name\":\"Springfield\",\"latitude\":39.8,\"longitude\":-89.6,\"admin1\":\"Illinois\"}]}";
    private const string Forecast = "{\"current\":{\"temperature_2m\":24.6,\"relative_humidity_2m\":61,\"weather_code\":2,\"wind_speed_10m\":12.3},"
        + "\"daily\":{\"time\":[\"2026-11-21\",\"2026-11-22\"],\"weather_code\":[2,61],\"temperature_2m_max\":[25.1,19.0],\"temperature_2m_min\":[14.2,11.5]}}";

    [Fact]
    public async Task The_weather_comes_from_open_meteo_by_city_in_the_chosen_unit()
    {
        var urls = new List<string>();
        var service = new WeatherService((url, _) => { urls.Add(url); return Task.FromResult(url.Contains("geocoding") ? Geocode : Forecast); });

        var report = await service.GetAsync("Springfield", fahrenheit: true);

        Assert.Equal("Springfield, Illinois", report!.Place);
        Assert.Equal("Partly cloudy", report.Condition);
        Assert.Equal("°F", report.Unit);
        Assert.Equal(2, report.Days.Count);
        Assert.Contains("temperature_unit=fahrenheit", urls[1]);
        Assert.StartsWith("https://geocoding-api.open-meteo.com/", urls[0]);   // open data, no key, nothing but the city name sent
        Assert.Null(await new WeatherService((_, _) => Task.FromResult("{\"results\":[]}")).GetAsync("Nowhereville", false));
        Assert.Null(await service.GetAsync("  ", false));
    }

    [Fact]
    public void Photos_from_this_day_are_the_files_dated_today_in_an_earlier_year()
    {
        var root = Path.Combine(Path.GetTempPath(), "otd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "trip"));
        try
        {
            var today = new DateOnly(2026, 11, 21);
            string Make(string name, DateTime when) { var p = Path.Combine(root, name); File.WriteAllText(p, "x"); File.SetLastWriteTime(p, when); return p; }
            var old = Make("trip/a.jpg", new DateTime(2023, 11, 21, 9, 0, 0));
            var older = Make("b.png", new DateTime(2019, 11, 21, 9, 0, 0));
            Make("c.jpg", new DateTime(2026, 11, 21, 9, 0, 0));    // this year: not "on this day"
            Make("d.jpg", new DateTime(2023, 11, 20, 9, 0, 0));    // another day
            Make("e.txt", new DateTime(2023, 11, 21, 9, 0, 0));    // not a picture

            Assert.Equal(new[] { old, older }, OnThisDay.Find([root], today));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Headlines_are_read_from_rss_and_atom_and_a_bad_feed_is_left_out()
    {
        const string rss = "<rss><channel><title>Daily</title><item><title>One</title><link>http://x/1</link><pubDate>Fri, 21 Nov 2026 08:00:00 GMT</pubDate></item><item><title>Two</title><link>http://x/2</link><pubDate>Thu, 20 Nov 2026 08:00:00 GMT</pubDate></item></channel></rss>";
        const string atom = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Blog</title><entry><title>Three</title><link href=\"http://y/3\"/><updated>2026-11-21T10:00:00Z</updated></entry></feed>";

        Assert.Equal(new[] { "One", "Two" }, FeedReader.Parse(rss).Select(i => i.Title));
        Assert.Equal("http://y/3", FeedReader.Parse(atom).Single().Link);
        Assert.Empty(FeedReader.Parse("<html><!DOCTYPE x [<!ENTITY a SYSTEM \"file:///etc/passwd\">]>"));   // no entity tricks, no crash

        var items = FeedReader.LoadAsync(["http://a/rss", "http://b/atom", "http://c/bad"], (url, _) => url.EndsWith("rss") ? Task.FromResult(rss) : url.EndsWith("atom") ? Task.FromResult(atom) : throw new HttpRequestException("down")).GetAwaiter().GetResult();
        Assert.Equal(new[] { "Three", "One", "Two" }, items.Select(i => i.Title));   // newest first across feeds
    }

    [Fact]
    public void A_card_an_app_sends_is_data_with_limits_and_unknown_kinds_are_skipped()
    {
        var card = WidgetCard.Parse("{\"title\":\"Tides\",\"rows\":[{\"type\":\"text\",\"text\":\"High 14:32\",\"size\":\"large\"},{\"type\":\"script\",\"code\":\"x\"},"
            + "{\"type\":\"progress\",\"value\":7},{\"type\":\"list\",\"items\":[{\"text\":\"a\",\"action\":\"open:1\"}]},{\"type\":\"button\",\"text\":\"Refresh\",\"action\":\"refresh\"}]}")!;

        Assert.Equal("Tides", card.Title);
        Assert.Equal(4, card.Rows.Count);                                   // the "script" row is skipped
        Assert.Equal("large", ((TextRow)card.Rows[0]).Size);
        Assert.Equal(1.0, ((ProgressRow)card.Rows[1]).Value);               // clamped
        Assert.Equal("open:1", ((ListRow)card.Rows[2]).Items.Single().Action);
        Assert.Null(WidgetCard.Parse("not json"));
        var many = "{\"rows\":[" + string.Join(",", Enumerable.Repeat("{\"type\":\"text\",\"text\":\"x\"}", 100)) + "]}";
        Assert.Equal(WidgetCard.MaxRows, WidgetCard.Parse(many)!.Rows.Count);
    }

    [Fact]
    public void Widget_settings_default_to_every_card_on_and_units_follow_the_choice()
    {
        var hive = new Vibe.Registry.Hive("HKCU");
        var settings = new WidgetSettings(hive);
        Assert.Equal(4, settings.Shown.Count);
        hive.SetValue(WidgetSettings.ShowKey, "Photos", Vibe.Registry.RegistryValueKind.DWord, 0u);
        Assert.DoesNotContain(settings.Shown, w => w.Id == "Photos");

        hive.SetValue(WidgetSettings.KeyPath, "WeatherUnits", Vibe.Registry.RegistryValueKind.String, "Fahrenheit");
        Assert.True(settings.Fahrenheit);
        hive.SetValue(WidgetSettings.KeyPath, "WeatherUnits", Vibe.Registry.RegistryValueKind.String, "Celsius");
        Assert.False(settings.Fahrenheit);
        hive.SetValue(WidgetSettings.KeyPath, "Feeds", Vibe.Registry.RegistryValueKind.String, "https://a/feed; ftp://b/x  http://c/y");
        Assert.Equal(new[] { "https://a/feed", "http://c/y" }, settings.Feeds);   // only web addresses
    }

    [Fact]
    public void The_panel_opens_at_the_left_above_the_taskbar_and_beside_a_side_bar()
    {
        var screen = new PixelBox(0, 0, 1920, 1080);
        Assert.Equal((12, 12, 1080 - 48 - 24), WidgetsPlacement.Compute(screen, TaskbarEdge.Bottom, 48, 640));
        Assert.Equal((12, 60, 1080 - 48 - 24), WidgetsPlacement.Compute(screen, TaskbarEdge.Top, 48, 640));
        Assert.Equal((60, 12, 1080 - 24), WidgetsPlacement.Compute(screen, TaskbarEdge.Left, 48, 640));
    }

    [Fact]
    public void An_app_offers_a_widget_through_two_keys_of_its_desktop_file_and_must_be_allowed_by_the_user()
    {
        var entry = Vde.Freedesktop.DesktopEntry.Parse("[Desktop Entry]\nType=Application\nName=Tides\nExec=tides\nX-VDE-Widget=org.example.Tides\nX-VDE-Widget-Path=/org/example/Tides\n", "org.example.Tides.desktop", []);
        Assert.Equal("org.example.Tides", entry.WidgetBusName);
        Assert.Equal("/org/example/Tides", entry.WidgetPath);
        Assert.Equal("/org/vde/Widget", Vde.Freedesktop.DesktopEntry.Parse("[Desktop Entry]\nType=Application\nName=A\nExec=a\nX-VDE-Widget=org.a.B\n", "a.desktop", []).WidgetPath);
        Assert.Null(Vde.Freedesktop.DesktopEntry.Parse("[Desktop Entry]\nType=Application\nName=A\nExec=a\n", "a.desktop", []).WidgetBusName);

        var hive = new Vibe.Registry.Hive("HKCU");
        var providers = new WidgetProviders(new Vibe.Registry.ClassesRoot(hive, new Vibe.Registry.Hive("HKLM")), hive);
        Assert.False(providers.IsAllowed("org.example.Tides.desktop"));    // nothing runs until this user says so
        providers.Remember([("org.example.Tides.desktop", "Tides")]);
        Assert.Equal("Tides", Assert.Single(WidgetProviders.Definitions(hive)).Label);
        providers.Allow("org.example.Tides.desktop", true);
        Assert.True(providers.IsAllowed("org.example.Tides.desktop"));
    }
}
