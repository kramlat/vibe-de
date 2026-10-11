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

        // One window, theme switched at runtime: this is what a user changing the theme does.
        var window = new Window { Width = 320, Height = 520, Content = panel, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "tiles-Light.png"));
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "tiles-Dark.png"));
    }
}

public class ThemeSwitchTests
{
    [AvaloniaFact]
    public void Tile_text_and_selection_follow_a_runtime_light_to_dark_switch()
    {
        var tile = DriveTile.For(new NetworkShare("photos on nas", "smb://nas/photos", "/mnt/nas", "cifs"));
        tile.IsSelected = true;
        var window = new Window { Width = 320, Height = 100, Content = tile, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        window.Show();

        var detail = tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "smb://nas/photos");
        var lightColor = ((ISolidColorBrush)detail.Foreground!).Color;

        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var darkColor = ((ISolidColorBrush)detail.Foreground!).Color;

        Assert.True(Brightness(lightColor) < 128, $"light theme secondary text should be dark, was {lightColor}");
        Assert.True(Brightness(darkColor) > 128, $"dark theme secondary text should be light, was {darkColor}");
        Assert.NotEqual(Brushes.Transparent, tile.BorderBrush);   // selected: accent outline in both themes
    }

    private static double Brightness(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000.0;
}

public class ThemeKeyTests
{
    // If Avalonia renames a brush, the controls would silently fall back to grey: catch that here.
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Every_resource_the_controls_use_exists_in_the_Fluent_theme(string variant)
    {
        var theme = variant == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        var window = new Window();
        foreach (var key in ThemeKeys.All)
            Assert.True(window.TryFindResource(key, theme, out var value) && value is IBrush, $"{key} missing in {variant}");
    }
}
