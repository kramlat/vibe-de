using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vibe.Registry;
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
            bmp.Save(Path.Combine(themeDir, "16", "blue.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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

        Assert.IsAssignableFrom<Bitmap>(provider.Load(["red"], 16));   // SVG, rasterized at load
        Assert.IsAssignableFrom<Bitmap>(provider.Load(["blue"], 16));
        Assert.IsAssignableFrom<Bitmap>(provider.Load(["no-such-icon"], 16));   // generic fallback
        Assert.Same(provider.Load(["red"], 16), provider.Load(["red"], 16));   // cached
    }

    [AvaloniaFact]
    public void Prefers_earlier_names_and_survives_broken_files()
    {
        using var fx = new IconFixture();
        var provider = fx.Provider();

        Assert.IsAssignableFrom<Bitmap>(provider.Load(["missing", "blue", "red"], 16));
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
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"tiles-{theme}-{variant}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
    public void The_file_list_shows_every_view_grouped_and_headings_are_never_selected()
    {
        var list = new FileListView { Entries = [E("b.txt"), E("a.txt"), E("dir", true), E("c.png")] };
        var window = new Window { Width = 700, Height = 400, Content = list };
        window.Show();
        foreach (var mode in Enum.GetValues<ViewMode>())
        {
            list.ViewMode = mode;
            list.GroupBy = GroupBy.Type;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(["File folder", "PNG file", "TXT file"], list.GroupTitles);
            list.SelectAll();
            Assert.Equal(4, list.SelectedEntries.Count);
            Assert.DoesNotContain(list.InnerList.SelectedItems!.Cast<object>(), x => x is not FileEntry);
            list.GroupBy = GroupBy.None;
            Assert.Empty(list.GroupTitles);
        }
    }

    [AvaloniaFact]
    public void A_drive_entry_shows_its_fullness_bar_in_tiles()
    {
        var drive = new FileEntry("Root", "/", true, false, false, 100, default, "") { IconNames = ["drive-harddisk"], Usage = 0.4, Detail = "60 GB free of 100 GB" };
        var list = new FileListView { Entries = [drive], ViewMode = ViewMode.Tiles };
        var window = new Window { Width = 700, Height = 300, Content = list };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(list.GetVisualDescendants().OfType<UsageBar>());
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
        Assert.Equal(true, hive.GetValue(@"Software\Vde\Notifications", "DoNotDisturb")!.Data);   // written straight to the registry
        timeout.Value = 8000;
        Assert.Equal(8000, hive.GetValue(@"Software\Vde\Notifications", "DefaultTimeoutMs")!.Data);
        timeout.Value = 10;                                                                      // below the 500 minimum: refused
        Assert.Equal(8000, hive.GetValue(@"Software\Vde\Notifications", "DefaultTimeoutMs")!.Data);

        hive.SetValue(@"Software\Vde\Notifications", "DoNotDisturb", Vibe.Registry.RegistryValueKind.DWord, 0u);   // a script flips it
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(dnd.IsChecked);
    }

    [AvaloniaFact]
    public void The_taskbar_page_has_items_corner_icons_and_behaviors_sections_whose_settings_reach_the_taskbar()
    {
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        var view = new SettingsPageView(BuiltInPages.Taskbar, hive);
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();

        Assert.Equal(new[] { "Taskbar items", "Taskbar corner icons", "Taskbar corner overflow", "Taskbar behaviors" }, view.SectionBodies.Keys);
        var edge = (ComboBox)view.ControlFor("edge");
        var position = (ComboBox)view.ControlFor("alignment");
        Assert.Equal(0, edge.SelectedIndex);       // Bottom
        Assert.Equal(0, position.SelectedIndex);   // Centered

        edge.SelectedIndex = 2;                    // Left
        position.SelectedIndex = 1;                // Aligned to the start
        Assert.Equal("Left", hive.GetValue(TaskbarModel.KeyPath, "Edge")!.Data);
        Assert.Equal("Start", hive.GetValue(TaskbarModel.KeyPath, "Alignment")!.Data);

        var model = new TaskbarModel(hive, Vde.Freedesktop.DesktopEntryDatabase.Load(new XdgDirectories(k => k switch { "HOME" => "/nonexistent-home", "XDG_DATA_HOME" => "/nonexistent-data", "XDG_DATA_DIRS" => "/nonexistent-system", _ => null }), []), new NoWindowSource(), _ => { });
        Assert.Equal(TaskbarEdge.Left, model.Edge);
        Assert.Equal(TaskbarAlignment.Start, model.Alignment);

        ((ToggleSwitch)view.ControlFor("touchkeyboard")).IsChecked = true;
        ((ToggleSwitch)view.ControlFor("search")).IsChecked = false;
        Assert.True(model.ShowTouchKeyboard);
        Assert.False(model.ShowSearch);
        Assert.True(model.ShowTaskView);   // untouched: still the default

        var header = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "section:Taskbar behaviors"));
        Assert.True(view.SectionBodies["Taskbar behaviors"].IsVisible);
        header.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(view.SectionBodies["Taskbar behaviors"].IsVisible);   // the chevron folds it
    }

    [AvaloniaFact]
    public void The_taskbar_page_lists_remembered_tray_icons_and_toggling_one_moves_it_to_the_overflow()
    {
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        TrayPreferences.Remember(hive, "steam", "Steam", "steam");
        var view = new SettingsPageView(BuiltInPages.Taskbar, hive);
        var window = new Window { Width = 700, Height = 700, Content = view };
        window.Show();

        var toggle = (ToggleSwitch)view.ControlFor("tray-steam");
        Assert.True(toggle.IsChecked);   // in the corner until the user says otherwise
        toggle.IsChecked = false;
        Assert.False(TrayPreferences.InCorner(hive, "steam"));

        TrayPreferences.Remember(hive, "nm-applet", "Network", "network-wired");   // a new icon shows up while the page is open
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.ControlFor("tray-nm-applet"));
        Assert.False(((ToggleSwitch)view.ControlFor("tray-steam")).IsChecked);

        var autohide = (CheckBox)view.ControlFor("autohide");
        autohide.IsChecked = true;
        Assert.Equal(true, hive.GetValue(TaskbarModel.KeyPath, "AutoHide")!.Data);
    }

    [AvaloniaFact]
    public async Task The_fonts_page_shows_the_installed_fonts_filters_them_and_installs_locally_or_for_everyone()
    {
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        var fonts = FontCatalog.Parse("Arial\tRegular\t/f/a.ttf\ten\nArial\tBold\t/f/b.ttf\ten\nAGA Arabesque\tRegular\t/f/c.ttf\tar\n");
        var installs = new List<(int Count, bool Local)>();
        var view = new FontsPageView(hive, (paths, local) => { installs.Add((paths.Count, local)); return Task.FromResult("done"); }, () => fonts);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();

        Assert.Equal(2, view.Tiles.Count);
        Assert.Equal("Install Locally", view.InstallLocallyBox.Content);
        Assert.True(view.InstallLocallyBox.IsChecked);   // for the current user, by default

        view.SearchText = "arial";
        Assert.Equal(new[] { "Arial" }, view.Shown.Select(f => f.Name));
        view.SearchText = "";
        view.LanguageBox.SelectedIndex = view.LanguageBox.Items.Cast<string>().ToList().IndexOf("Arabic");
        Assert.Equal(new[] { "AGA Arabesque" }, view.Shown.Select(f => f.Name));

        await view.InstallAsync(["/x/a.ttf"]);
        view.InstallLocallyBox.IsChecked = false;        // unchecked: every user, the system install
        await view.InstallAsync(["/x/a.ttf", "/x/b.otf"]);
        Assert.Equal(new[] { (1, true), (2, false) }, installs);
        Assert.False(FontInstaller.InstallLocally(hive));
        Assert.Equal("done", view.Status);
    }

    [AvaloniaFact]
    public async Task The_widgets_panel_shows_the_clock_search_cards_headlines_and_community_widgets()
    {
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        hive.SetValue(WidgetSettings.KeyPath, "Feeds", Vibe.Registry.RegistryValueKind.String, "https://news.example/rss");
        hive.SetValue(WidgetSettings.KeyPath, "WeatherCity", Vibe.Registry.RegistryValueKind.String, "");
        var opened = new List<string>();
        Fetch fetch = (url, _) => Task.FromResult("<rss><channel><title>Daily</title><item><title>Big story</title><link>http://x/1</link></item></channel></rss>");
        var community = new CommunityWidget("tides", "Tides", ["weather-clear"], () => new TextBlock { Text = "High 14:32" });
        var panel = new WidgetsPanelView(hive, opened.Add, opened.Add, () => opened.Add("settings"), fetch, () => new DateTime(2026, 11, 21, 15, 0, 0), [], () => [community]);
        var window = new Window { Width = 700, Height = 900, Content = panel };
        window.Show();
        await Task.Delay(100);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("3", panel.ClockText);
        Assert.Equal(new[] { "Photos", "System", "Weather", "tides" }, panel.CardIds.Order(StringComparer.Ordinal));    // ours and then the community's, in two columns
        Assert.Equal(new[] { "http://x/1" }, panel.Headlines);

        panel.SearchText = "open source widgets";
        panel.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "search")).RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal("https://duckduckgo.com/?q=open%20source%20widgets", opened.Single());   // an open search engine

        RegistryBoolSet(hive, "Photos", false);                                               // hide a card from the settings
        panel.Rebuild();
        Assert.DoesNotContain("Photos", panel.CardIds);
    }

    private static void RegistryBoolSet(Vibe.Registry.IHive hive, string id, bool on) => RegistryBool.Set(hive, WidgetSettings.ShowKey, id, on);

    [AvaloniaFact]
    public void A_card_an_app_sent_is_drawn_with_the_shells_controls_and_a_press_names_the_action()
    {
        var card = WidgetCard.Parse("{\"rows\":[{\"type\":\"text\",\"text\":\"Hello\"},{\"type\":\"progress\",\"value\":0.5,\"label\":\"Half\"},"
            + "{\"type\":\"list\",\"items\":[{\"text\":\"Item\",\"action\":\"open:1\"}]},{\"type\":\"button\",\"text\":\"Refresh\",\"action\":\"refresh\"}]}")!;
        var pressed = new List<string>();
        var view = WidgetCardView.Build(card, pressed.Add);
        var window = new Window { Content = view };
        window.Show();

        Assert.Contains("Hello", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Equal(0.5, view.GetVisualDescendants().OfType<ProgressBar>().Single().Value);
        foreach (var button in view.GetVisualDescendants().OfType<Button>())
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "open:1", "refresh" }, pressed);
    }

    [AvaloniaFact]
    public void The_launcher_footer_shows_the_user_and_has_files_settings_and_power_buttons()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vde-foot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var passwd = Path.Combine(dir, "passwd");
        File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/sh\nmark:x:1000:1000:Mark Toman,,,:/home/mark:/bin/bash\n");
        try
        {
            var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
            var model = new LauncherModel(hive, Vde.Freedesktop.DesktopEntryDatabase.Load(TestTheme.EmptyXdg(dir), []), ["VDE"]);
            var user = new UserProfile(k => k == "USER" ? "mark" : k == "HOME" ? dir : null, passwd, Path.Combine(dir, "icons"));
            var view = new LauncherView(model, user);
            new Window { Width = 400, Height = 600, Content = view }.Show();
            var actions = new List<LauncherFooterAction>();
            view.FooterInvoked += (_, a) => actions.Add(a);

            Assert.Equal("Mark Toman", view.UserDisplayName);
            Button Find(string tag) => view.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Tag == tag);
            void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Click(Find("footer:Files"));
            Click(Find("footer:Settings"));
            Assert.Equal([LauncherFooterAction.Files, LauncherFooterAction.Settings], actions);

            Assert.False(view.PowerChoicesVisible);
            Click(Find("footer:Power"));
            Assert.True(view.PowerChoicesVisible);
            actions.Clear();
            Click(Find("power:Shut down"));
            Assert.Equal([LauncherFooterAction.ShutDown], actions);
            Assert.False(view.PowerChoicesVisible);   // choosing closes the list
            Assert.DoesNotContain("footer:Files", view.VisibleTitles);   // the footer is not a result row
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void Launcher_footer_picture()
    {
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is not { Length: > 0 } shots) return;
        var dir = Path.Combine(Path.GetTempPath(), "vde-footpic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
            var model = new LauncherModel(hive, Vde.Freedesktop.DesktopEntryDatabase.Load(TestTheme.EmptyXdg(dir), []), ["VDE"]);
            var view = new LauncherView(model, new UserProfile(k => k == "USER" ? "mark" : null, Path.Combine(dir, "none"), dir));
            var window = new Window { Width = 460, Height = 320, Content = view };
            window.Show();
            view.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Tag == "footer:Power").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.CaptureRenderedFrame()!.Save(Path.Combine(shots, "launcher-footer.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_user_profile_reads_the_full_name_and_finds_the_account_picture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vde-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var passwd = Path.Combine(dir, "passwd");
        File.WriteAllText(passwd, "ann:x:1001:1001:,,,:/home/ann:/bin/sh\nmark:x:1000:1000:Mark Toman,Room 1:/home/mark:/bin/bash\n");
        try
        {
            Func<string, string?> Env(string name) => k => k == "USER" ? name : k == "HOME" ? dir : null;
            var mark = new UserProfile(Env("mark"), passwd, Path.Combine(dir, "icons"));
            Assert.Equal("Mark Toman", mark.DisplayName);
            Assert.Equal("M", mark.Initial);
            Assert.Null(mark.PicturePath);
            File.WriteAllText(Path.Combine(dir, ".face"), "x");
            Assert.Equal(Path.Combine(dir, ".face"), mark.PicturePath);

            var ann = new UserProfile(Env("ann"), passwd, Path.Combine(dir, "icons"));
            Assert.Equal("ann", ann.DisplayName);   // no full name: the account name
            Assert.Equal("A", ann.Initial);
            Assert.Equal("nobody", new UserProfile(Env("nobody"), Path.Combine(dir, "missing")).DisplayName);
        }
        finally { Directory.Delete(dir, recursive: true); }
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
            Assert.Contains("Colors", view.VisibleTitles);                                          // settings come up in the same search
            view.SearchText = "qqqq";
            Assert.Empty(view.VisibleTitles);                                                       // and nothing from anywhere else
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class ShortcutInputTests
{
    [Fact]
    public void Avalonias_key_names_match_the_shortcut_tables_names()
    {
        Assert.Equal("Enter", ShortcutInput.KeyName(Avalonia.Input.Key.Return));
        Assert.Equal("Enter", ShortcutInput.KeyName(Avalonia.Input.Key.Enter));
        Assert.Equal("Back", ShortcutInput.KeyName(Avalonia.Input.Key.Back));
        Assert.Equal("OemOpenBrackets", ShortcutInput.KeyName(Avalonia.Input.Key.OemOpenBrackets));
        Assert.Equal("C", ShortcutInput.KeyName(Avalonia.Input.Key.C));
        Assert.Equal("F2", ShortcutInput.KeyName(Avalonia.Input.Key.F2));
        // every key the tables use must be a real Avalonia key (or Enter), or the shortcut could never fire
        foreach (var style in new[] { ShortcutStyle.Pc, ShortcutStyle.Mac })
            foreach (var action in Enum.GetValues<ShortcutAction>())
                foreach (var shortcut in ShortcutMap.For(action, style))
                    Assert.True(shortcut.Key == "Enter" || Enum.TryParse<Avalonia.Input.Key>(shortcut.Key, out _), $"{shortcut.Key} is not an Avalonia Key");
    }
}
