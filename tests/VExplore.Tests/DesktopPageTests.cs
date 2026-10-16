using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vde.Shell;
using Vde.Shell.Controls;
using Vibe.Registry;
using VExplore.Windows;

namespace VExplore.Tests;

public class DesktopPageTests
{
    private sealed class Pictures : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vexplore-wp-" + Guid.NewGuid().ToString("N"));
        public Pictures()
        {
            Directory.CreateDirectory(Dir);
            Make("Dunes.png", 0xFFC8A060);
            Make("Ocean.png", 0xFF2060C0);
        }
        public string Path_(string name) => Path.Combine(Dir, name);
        private void Make(string name, uint argb)
        {
            using var bmp = new WriteableBitmap(new PixelSize(64, 36), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock())
            {
                var row = Enumerable.Repeat(argb, 64).SelectMany(BitConverter.GetBytes).ToArray();
                for (var y = 0; y < 36; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
            bmp.Save(Path_(name));
        }
        public IReadOnlyList<WallpaperInfo> Catalog => [new("Dunes", Path_("Dunes.png")), new("Ocean", Path_("Ocean.png"))];
        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [AvaloniaFact]
    public void Choosing_a_wallpaper_saves_it_updates_the_preview_and_marks_the_tile()
    {
        using var pics = new Pictures();
        var hive = new Hive("HKCU");
        var page = new DesktopPageView(hive, pics.Catalog);
        var window = new Window { Content = page, Width = 700, Height = 700 };
        window.Show();

        Assert.Equal(new[] { "None", "Dunes", "Ocean" }, page.Tiles.Select(t => t.Tag as string));
        Assert.IsType<SolidColorBrush>(page.Preview.Background);   // no wallpaper yet: the colour

        page.Tiles[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(pics.Path_("Ocean.png"), page.Settings.Wallpaper);
        Assert.IsType<ImageBrush>(page.Preview.Background);
        Assert.NotEqual(Brushes.Transparent, page.Tiles[2].BorderBrush);   // the chosen tile is outlined
        Assert.Equal(Brushes.Transparent, page.Tiles[1].BorderBrush);

        page.Tiles[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // None
        Assert.Null(page.Settings.Wallpaper);
        Assert.IsType<SolidColorBrush>(page.Preview.Background);
    }

    [AvaloniaFact]
    public void Placement_colour_and_icons_are_saved_and_a_bad_colour_is_refused()
    {
        var hive = new Hive("HKCU");
        var page = new DesktopPageView(hive, []);
        page.SetMode(WallpaperMode.Tile);
        page.SetShowIcons(false);
        Assert.True(page.SetColor("#336699"));

        var saved = DesktopSettingsStore.Load(hive);
        Assert.Equal(WallpaperMode.Tile, saved.Mode);
        Assert.False(saved.ShowIcons);
        Assert.Equal("#336699", saved.BackgroundColor);

        Assert.False(page.SetColor("blue"));
        Assert.NotNull(page.ColorError);
        Assert.Equal("#336699", DesktopSettingsStore.Load(hive).BackgroundColor);   // unchanged
    }

    [AvaloniaFact]
    public async Task Browse_uses_the_chosen_file_and_keeps_it_in_the_gallery()
    {
        using var pics = new Pictures();
        var hive = new Hive("HKCU");
        var page = new DesktopPageView(hive, [], _ => Task.FromResult<string?>(pics.Path_("Dunes.png")));
        new Window { Content = page }.Show();

        await page.BrowseAsync();

        Assert.Equal(pics.Path_("Dunes.png"), page.Settings.Wallpaper);
        Assert.Contains(page.Tiles, t => (string?)t.Tag == "Dunes");   // not in the catalogue, but shown
    }

    [AvaloniaFact]
    public void The_desktop_window_follows_the_setting_and_the_control_panel_opens_the_page()
    {
        using var pics = new Pictures();
        var hive = new Hive("HKCU") { Identity = RegistryIdentity.Root };
        var home = Path.Combine(pics.Dir, "Desktop");
        Directory.CreateDirectory(home);
        var desktop = new DesktopWindow(home, hive, _ => { });
        desktop.Show();
        Assert.IsType<SolidColorBrush>(desktop.Background);

        new DesktopPageView(hive, pics.Catalog).SelectWallpaper(pics.Path_("Dunes.png"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsType<ImageBrush>(desktop.Background);

        var catalog = ControlPanelCatalog.Load(null, hive, null, []);
        var panel = new ControlPanelWindow(catalog, hive, _ => { }, customPages: id => id == "desktop" ? new DesktopPageView(hive, pics.Catalog) : null);
        panel.Show();
        panel.Open(catalog.Items.Single(i => i.Id == "desktop"));
        Assert.Equal("desktop", panel.OpenPageId);
    }

    [AvaloniaFact]
    public void Desktop_page_picture()
    {
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is not { Length: > 0 } dir) return;
        using var pics = new Pictures();
        var hive = new Hive("HKCU");
        var page = new DesktopPageView(hive, pics.Catalog, _ => Task.FromResult<string?>(null));
        page.SelectWallpaper(pics.Path_("Ocean.png"));
        var window = new Window { Content = page, Width = 560, Height = 620 };
        window.Show();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "desktop-page.png"));
    }
}
