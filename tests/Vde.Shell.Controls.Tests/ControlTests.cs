using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.VisualTree;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls.Tests;

// A throwaway icon theme: "red" is an SVG, "blue" a PNG, "application-x-generic" the fallback.
public sealed class IconFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-ctl-" + Guid.NewGuid().ToString("N"));
    public IconThemeLookup Lookup { get; }

    public IconFixture()
    {
        var themeDir = Path.Combine(Root, "data", "icons", "hicolor");
        Directory.CreateDirectory(Path.Combine(themeDir, "16"));
        File.WriteAllText(Path.Combine(themeDir, "index.theme"),
            "[Icon Theme]\nName=hicolor\nDirectories=16\n\n[16]\nSize=16\nType=Scalable\nMinSize=8\nMaxSize=512\n");
        File.WriteAllText(Path.Combine(themeDir, "16", "red.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><rect width=\"16\" height=\"16\" fill=\"#ff0000\"/></svg>");
        File.WriteAllText(Path.Combine(themeDir, "16", "application-x-generic.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><rect width=\"16\" height=\"16\" fill=\"#00ff00\"/></svg>");
        File.WriteAllText(Path.Combine(themeDir, "16", "broken.svg"), "this is not svg");

        using (var bmp = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul))
        {
            using (var fb = bmp.Lock())
                for (int i = 0; i < 16 * 16; i++)
                    System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, i * 4, unchecked((int)0xFF0000FF));   // opaque blue, BGRA
            bmp.Save(Path.Combine(themeDir, "16", "blue.png"));
        }

        var env = new Dictionary<string, string> { ["HOME"] = Root, ["XDG_DATA_HOME"] = Path.Combine(Root, "data"), ["XDG_DATA_DIRS"] = Path.Combine(Root, "none") };
        Lookup = new IconThemeLookup(new XdgDirectories(k => env.GetValueOrDefault(k)));
    }

    public ShellIconProvider Provider() => new(Lookup, "hicolor");

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class ShellIconProviderTests
{
    [AvaloniaFact]
    public void Loads_svg_png_and_falls_back_to_the_generic_icon()
    {
        using var fx = new IconFixture();
        var provider = fx.Provider();

        Assert.IsType<SvgImage>(provider.Load(["red"], 16));
        Assert.IsType<Bitmap>(provider.Load(["blue"], 16));
        Assert.IsType<SvgImage>(provider.Load(["no-such-icon"], 16));   // generic fallback
        Assert.Same(provider.Load(["red"], 16), provider.Load(["red"], 16));   // cached
    }

    [AvaloniaFact]
    public void Prefers_earlier_names_and_survives_broken_files()
    {
        using var fx = new IconFixture();
        var provider = fx.Provider();

        Assert.IsType<Bitmap>(provider.Load(["missing", "blue", "red"], 16));
        // "broken" exists but cannot be parsed: no image, and no exception for the caller
        Assert.Null(provider.Load(["broken"], 16));
    }
}

public class ShellIconTests
{
    internal static (uint r, uint g, uint b) Pixel(WriteableBitmap frame, int x, int y)
    {
        using var fb = frame.Lock();
        int offset = y * fb.RowBytes + x * 4;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address + offset, bytes, 0, 4);
        // Skia on Linux is Bgra8888; Rgba8888 swaps the first and third byte.
        return frame.Format?.ToString() == "Rgba8888" ? (bytes[0], bytes[1], bytes[2]) : (bytes[2], bytes[1], bytes[0]);
    }

    [AvaloniaFact]
    public void Draws_the_icon_filling_its_square()
    {
        using var fx = new IconFixture();
        ShellIconProvider.Default = fx.Provider();
        var icon = new ShellIcon { IconName = "red", IconSize = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 64, Height = 64, Content = icon, Background = Brushes.White };
        window.Show();

        var frame = window.CaptureRenderedFrame()!;

        Assert.Equal((255u, 0u, 0u), Pixel(frame, 16, 16));   // inside the icon
        Assert.Equal((255u, 255u, 255u), Pixel(frame, 50, 50));   // outside it
        Assert.Equal(new Size(32, 32), icon.DesiredSize);
    }

    [AvaloniaFact]
    public void Changing_the_name_or_theme_redraws()
    {
        using var fx = new IconFixture();
        var provider = fx.Provider();
        ShellIconProvider.Default = provider;
        var icon = new ShellIcon { IconName = "red", IconSize = 16, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 32, Height = 32, Content = icon, Background = Brushes.White };
        window.Show();
        Assert.Equal((255u, 0u, 0u), Pixel(window.CaptureRenderedFrame()!, 8, 8));

        icon.IconName = "blue";
        Assert.Equal((0u, 0u, 255u), Pixel(window.CaptureRenderedFrame()!, 8, 8));

        icon.IconNames = null;
        Assert.Equal((255u, 255u, 255u), Pixel(window.CaptureRenderedFrame()!, 8, 8));
    }
}

public class DriveTileTests
{
    [Fact]
    public void Formats_sizes_like_explorer()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("0 bytes", ByteFormat.Format(0, c));
        Assert.Equal("1 byte", ByteFormat.Format(1, c));
        Assert.Equal("1023 bytes", ByteFormat.Format(1023, c));
        Assert.Equal("1.00 KB", ByteFormat.Format(1024, c));
        Assert.Equal("1.46 GB", ByteFormat.Format(1_570_000_000, c));
        Assert.Equal("14.6 GB", ByteFormat.Format(15_700_000_000, c));
        Assert.Equal("146 GB", ByteFormat.Format(157_000_000_000, c));
        Assert.Equal("1.00 TB", ByteFormat.Format(1L << 40, c));
    }

    [Fact]
    public void Volumes_map_to_icon_names()
    {
        Volume V(VolumeKind k, string mp) => new("/dev/x", mp, "ext4", "L", k, 1, 1, false);
        Assert.Equal("drive-harddisk-root", V(VolumeKind.Internal, "/").Names()[0]);
        Assert.Equal("drive-harddisk", V(VolumeKind.Internal, "/home").Names()[0]);
        Assert.Equal("drive-removable-media-usb", V(VolumeKind.Removable, "/run/media/x").Names()[0]);
        Assert.Equal("media-optical", V(VolumeKind.Optical, "/run/media/d").Names()[0]);
        Assert.Equal("folder-remote", V(VolumeKind.Network, "/mnt/n").Names()[0]);
    }

    [AvaloniaFact]
    public void Tile_for_a_volume_shows_name_usage_and_free_space()
    {
        var tile = DriveTile.For(new Volume("/dev/sda1", "/", "ext4", "System", VolumeKind.Internal, 100_000_000_000, 25_000_000_000, false));

        Assert.Equal("System", tile.Title);
        Assert.Equal(0.75, tile.Usage, 3);
        Assert.Contains("free of", tile.Detail);
        Assert.Equal(new[] { "drive-harddisk-root", "drive-harddisk" }, tile.IconNames);
    }

    [AvaloniaFact]
    public void Tile_without_size_info_hides_the_bar_and_shares_show_their_url()
    {
        var volume = DriveTile.For(new Volume("/dev/x", "/mnt/x", "fuseblk", "X", VolumeKind.Internal, 0, 0, false));
        Assert.True(double.IsNaN(volume.Usage));
        Assert.Equal("/mnt/x", volume.Detail);

        var share = DriveTile.For(new NetworkShare("photos on nas", "smb://nas/photos", "/mnt/nas", "cifs"));
        Assert.Equal("smb://nas/photos", share.Detail);
        Assert.True(double.IsNaN(share.Usage));
    }

    [AvaloniaFact]
    public void Usage_bar_is_critical_coloured_when_nearly_full()
    {
        Assert.Equal(0.5, new UsageBar { Value = 0.5 }.Value);
        var window = new Window { Width = 200, Height = 30, Background = Brushes.White, Content = new UsageBar { Value = 0.95, Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } };
        window.Show();

        var (r, g, b) = ShellIconTests.Pixel(window.CaptureRenderedFrame()!, 20, 15);

        Assert.True(r > g + 40 && r > b + 40, $"expected a red-ish fill, got {r},{g},{b}");
    }

    // Writes a picture of real tiles with the machine's own Breeze icons, for a human to look at.
    [AvaloniaFact]
    public void Gallery_screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir))
            return;
        ShellIconProvider.Default = new ShellIconProvider();
        Volume V(string label, VolumeKind k, string mp, long total, long free) => new("/dev/x", mp, "ext4", label, k, total, free, false);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 4 };
        panel.Children.Add(DriveTile.For(V("System", VolumeKind.Internal, "/", 53_000_000_000, 8_000_000_000)));
        panel.Children.Add(DriveTile.For(V("home", VolumeKind.Internal, "/home", 944_000_000_000, 91_000_000_000)));
        panel.Children.Add(DriveTile.For(V("My Stick", VolumeKind.Removable, "/run/media/x", 32_000_000_000, 3_000_000_000)));
        panel.Children.Add(DriveTile.For(V("DISC", VolumeKind.Optical, "/run/media/d", 700_000_000, 0)));
        panel.Children.Add(DriveTile.For(new NetworkShare("photos on nas", "smb://nas.local/photos", "/mnt/nas", "cifs")));
        var selected = DriveTile.For(V("Selected", VolumeKind.Internal, "/data", 500_000_000_000, 250_000_000_000));
        selected.IsSelected = true;
        panel.Children.Add(selected);

        // A new window per theme: windows opened after a base-theme swap are fully re-styled.
        foreach (var theme in new[] { "fluent", "simple" })
            foreach (var variant in new[] { VariantPreference.Light, VariantPreference.Dark })
            {
                TestTheme.Engine.Apply(new ThemeSelection(theme, variant));
                var window = new Window { Width = 320, Height = 520, Content = panel };
                window.Show();
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"tiles-{theme}-{variant}.png"));
                window.Content = null;
                window.Close();
            }
    }
}

public class ThemeIntegrationTests
{
    private static double Brightness(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000.0;

    private static (DriveTile tile, Window window, TextBlock detail) Show()
    {
        var tile = DriveTile.For(new NetworkShare("photos on nas", "smb://nas/photos", "/mnt/nas", "cifs"));
        tile.IsSelected = true;
        var window = new Window { Width = 320, Height = 100, Content = tile };
        window.Show();
        return (tile, window, tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "smb://nas/photos"));
    }

    [AvaloniaFact]
    public void Tile_text_follows_a_runtime_light_to_dark_switch()
    {
        var (_, _, detail) = Show();

        TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Light));
        var light = ((ISolidColorBrush)detail.Foreground!).Color;
        TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Dark));
        var dark = ((ISolidColorBrush)detail.Foreground!).Color;

        Assert.True(Brightness(light) < 128, $"light theme secondary text should be dark, was {light}");
        Assert.True(Brightness(dark) > 128, $"dark theme secondary text should be light, was {dark}");
    }

    [AvaloniaFact]
    public void Tile_follows_a_theme_change_that_keeps_the_same_variant()
    {
        var (tile, _, _) = Show();

        TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Light));
        var fluentBorder = ((ISolidColorBrush)tile.BorderBrush!).Color;
        TestTheme.Engine.Apply(new ThemeSelection("simple", VariantPreference.Light));   // no light/dark change at all
        var simpleBorder = ((ISolidColorBrush)tile.BorderBrush!).Color;

        Assert.NotEqual(fluentBorder, simpleBorder);
    }

    [AvaloniaFact]
    public void A_custom_themes_tokens_reach_the_usage_bar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vde-ctl-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "vde", "themes", "limes"));
            File.WriteAllText(Path.Combine(dir, "vde", "themes", "limes", "index.theme"),
                "[VDE Theme]\nBase=Fluent\n[Tokens]\nAccent=#00ff00\nCritical=#ffff00\n");
            TestTheme.Reset(ThemeCatalog.Discover(TestTheme.EmptyXdg(dir)));

            var bar = new UsageBar { Value = 0.5, Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var window = new Window { Width = 200, Height = 30, Background = Brushes.White, Content = bar };
            window.Show();

            TestTheme.Engine.Apply(new ThemeSelection("limes"));
            var normal = ShellIconTests.Pixel(window.CaptureRenderedFrame()!, 20, 15);
            bar.Value = 0.95;
            var critical = ShellIconTests.Pixel(window.CaptureRenderedFrame()!, 20, 15);

            Assert.Equal((0u, 255u, 0u), normal);
            Assert.Equal((255u, 255u, 0u), critical);
        }
        finally
        {
            TestTheme.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Changing_the_registry_re_themes_the_controls_live()
    {
        var (tile, _, detail) = Show();
        var hive = new Vibe.Registry.Hive("HKCU");
        using (TestTheme.Engine.BindTo(hive))
        {
            ThemeSettings.Save(hive, new ThemeSelection("fluent", VariantPreference.Dark));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(Brightness(((ISolidColorBrush)detail.Foreground!).Color) > 128);

            ThemeSettings.Save(hive, new ThemeSelection("fluent", VariantPreference.Light, Color.Parse("#ff6a00")));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(Brightness(((ISolidColorBrush)detail.Foreground!).Color) < 128);
            Assert.Equal(Color.Parse("#ff6a00"), ((ISolidColorBrush)tile.BorderBrush!).Color);   // the accent from the registry
        }
    }
}


public class BaseThemeSwapTests
{
    private static string Bg(Window w) => $"{(w.Background as ISolidColorBrush)?.Color}";

    [AvaloniaFact]
    public void A_window_opened_after_switching_to_simple_dark_gets_simple_dark()
    {
        TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Light));
        TestTheme.Engine.Apply(new ThemeSelection("simple", VariantPreference.Dark));

        var window = new Window { Content = new TextBlock { Text = "hi" } };
        window.Show();

        Assert.Equal("#ff282828", Bg(window));
    }

    [AvaloniaFact]
    public void The_host_is_told_when_the_base_theme_changes_but_not_for_variant_or_colour_changes()
    {
        TestTheme.Engine.Apply(new ThemeSelection("fluent"));
        int baseChanges = 0;
        void Count(object? s, EventArgs e) => baseChanges++;
        TestTheme.Engine.BaseThemeChanged += Count;
        try
        {
            TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Dark));
            TestTheme.Engine.Apply(new ThemeSelection("fluent", VariantPreference.Dark, Color.Parse("#ff6a00")));
            Assert.Equal(0, baseChanges);

            TestTheme.Engine.Apply(new ThemeSelection("simple", VariantPreference.Dark));
            Assert.Equal(1, baseChanges);
            TestTheme.Engine.Apply(new ThemeSelection("simple", VariantPreference.Light));
            Assert.Equal(1, baseChanges);
        }
        finally { TestTheme.Engine.BaseThemeChanged -= Count; }
    }
}

public class FileManagerControlTests
{
    private static FileEntry E(string name, bool dir = false, long size = 10) => new(name, "/x/" + name, dir, false, false, size, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), dir ? "" : Path.GetExtension(name));

    [AvaloniaFact]
    public void The_file_list_sorts_flips_on_a_second_click_and_reports_invocation()
    {
        var list = new FileListView { Entries = [E("b.txt"), E("a.txt"), E("dir", true)] };
        var window = new Window { Width = 700, Height = 300, Content = list };
        window.Show();

        Assert.Equal(new[] { "dir", "a.txt", "b.txt" }, list.Entries.Select(e => e.Name));
        int changes = 0;
        list.SortChanged += (_, _) => changes++;
        list.Sort(SortKey.Name, true);
        Assert.Equal(new[] { "dir", "b.txt", "a.txt" }, list.Entries.Select(e => e.Name));   // folders stay first
        Assert.Equal(1, changes);

        IReadOnlyList<FileEntry>? invoked = null;
        list.ItemsInvoked += (_, items) => invoked = items;
        list.SelectAll();
        Assert.Equal(3, list.SelectedEntries.Count);
    }

    [AvaloniaFact]
    public void The_address_bar_shows_segments_navigates_on_click_and_parses_typed_text()
    {
        var bar = new AddressBar { Home = "/home/deck", Path = "/home/deck/Music" };
        var window = new Window { Width = 600, Height = 60, Content = bar };
        window.Show();
        var requested = new List<string>();
        var rejected = new List<string>();
        bar.PathRequested += (_, p) => requested.Add(p);
        bar.TextRejected += (_, t) => rejected.Add(t);

        var buttons = bar.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(new[] { "/", "home", "Home", "Music" }, buttons.Select(b => (string)b.Content!));
        buttons[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "/home/deck" }, requested);

        bar.BeginEdit();
        Assert.True(bar.IsEditing);
        Assert.Equal("/home/deck/Music", bar.EditText);
        bar.EditText = "~/Documents";
        bar.Commit();
        Assert.False(bar.IsEditing);
        Assert.Equal("/home/deck/Documents", requested[^1]);

        bar.BeginEdit(); bar.EditText = "smb://nas/share"; bar.Commit();
        Assert.Equal(new[] { "smb://nas/share" }, rejected);
    }

    [AvaloniaFact]
    public void The_places_sidebar_lists_places_and_raises_the_one_picked()
    {
        var sidebar = new PlacesSidebar { Places = [new Place("Home", ["user-home"], PlaceKind.Folder, "/home/deck"), new Place("Trash", ["user-trash"], PlaceKind.Trash)] };
        var window = new Window { Width = 220, Height = 300, Content = sidebar };
        window.Show();
        var picked = new List<Place>();
        sidebar.PlaceInvoked += (_, p) => picked.Add(p);

        sidebar.ShowCurrent("/home/deck");                       // highlighting the current folder is not a click
        Assert.Empty(picked);
        Assert.Equal("Home", sidebar.SelectedPlace!.Name);
        sidebar.SelectedPlace = sidebar.Places[1];
        Assert.Equal("Trash", Assert.Single(picked).Name);
    }

    [AvaloniaFact]
    public void A_settings_page_edits_the_registry_refuses_bad_values_and_follows_outside_changes()
    {
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        var view = new SettingsPageView(BuiltInPages.Notifications, hive);
        var window = new Window { Width = 500, Height = 500, Content = view };
        window.Show();

        var dnd = (ToggleSwitch)view.ControlFor("dnd");
        var timeout = (NumericUpDown)view.ControlFor("timeout");
        Assert.False(dnd.IsChecked);
        Assert.Equal(5000m, timeout.Value);

        dnd.IsChecked = true;
        Assert.Equal(1u, hive.GetValue(@"Software\Vde\Notifications", "DoNotDisturb")!.Data);   // written straight to the registry
        timeout.Value = 8000;
        Assert.Equal(8000u, hive.GetValue(@"Software\Vde\Notifications", "DefaultTimeoutMs")!.Data);
        timeout.Value = 10;                                                                      // below the 500 minimum: refused
        Assert.Equal(8000u, hive.GetValue(@"Software\Vde\Notifications", "DefaultTimeoutMs")!.Data);

        hive.SetValue(@"Software\Vde\Notifications", "DoNotDisturb", Vibe.Registry.RegistryValueKind.DWord, 0u);   // a script flips it
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(dnd.IsChecked);
    }

    [AvaloniaFact]
    public void The_launcher_shows_pinned_and_recent_all_apps_and_local_search_and_runs_what_is_picked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vde-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "applications"));
        foreach (var (id, name) in new[] { ("kate", "Kate"), ("zip", "Zip Tool") })
            File.WriteAllText(Path.Combine(dir, "applications", id + ".desktop"), $"[Desktop Entry]\nType=Application\nName={name}\nExec={id}\n");
        try
        {
            var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
            var apps = Vde.Freedesktop.DesktopEntryDatabase.Load(TestTheme.EmptyXdg(dir), []);
            var model = new LauncherModel(hive, apps, ["VDE"], ControlPanelCatalog.Load(null, hive, null, []));
            model.Pin("kate");
            var view = new LauncherView(model);
            var window = new Window { Width = 400, Height = 600, Content = view };
            window.Show();
            var started = new List<string>();
            view.AppInvoked += (_, app) => started.Add(app.Id);

            Assert.Contains("Kate", view.VisibleTitles);
            Assert.DoesNotContain("Zip Tool", view.VisibleTitles);

            view.ShowAllApps = true;
            Assert.Equal(new[] { "Kate", "Zip Tool" }, view.VisibleTitles);

            view.ShowAllApps = false;
            view.SearchText = "zip";
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Tag == "Zip Tool");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { "zip.desktop" }, started);
            Assert.Equal("zip.desktop", model.Recent[0].Id);                                       // launching records it

            view.SearchText = "dark";
            Assert.Contains("Appearance", view.VisibleTitles);                                      // settings come up in the same search
            view.SearchText = "qqqq";
            Assert.Empty(view.VisibleTitles);                                                       // and nothing from anywhere else
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
