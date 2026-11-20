using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vde.Shell;
using Vde.Shell.Controls;
using Avalonia.VisualTree;

namespace VExplore.Tests;

// The Mica material: the wallpaper mapping the plates and panels crop by must agree with what
// WallpaperBrush paints on the desktop, and the blurred plate must stay readable over any picture.
public class MicaTests
{
    private static string MakeWallpaper(int r, int g, int b)
    {
        var path = Path.Combine(Path.GetTempPath(), "vde-mica-" + Guid.NewGuid().ToString("N") + ".png");
        using var bmp = new WriteableBitmap(new PixelSize(200, 100), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
            for (int i = 0; i < 200 * 100; i++)
                System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, i * 4, unchecked((b << 24) | (g << 16) | (r << 8) | 0xFF));
        bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    [AvaloniaFact]   // needs the headless app up: WriteableBitmap and Application.Current (the wash's variant)
    public void Source_blurs_and_tints()
    {
        var path = MakeWallpaper(255, 255, 255);   // pure white wallpaper
        using var blurred = WallpaperMica.Source(path, width: 200);
        Assert.NotNull(blurred);
        Assert.Equal(new PixelSize(200, 100), blurred!.PixelSize);
        // Win11 recipe: the wash dominates. Over pure white the result is the light base #F3F3F3 pulled a
        // little toward the wallpaper: about 0.2*255 + 0.8*243 = 245, alpha fully opaque
        var data = new byte[200 * 4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            blurred.CopyPixels(new PixelRect(0, 50, 200, 1), pinned.AddrOfPinnedObject(), data.Length, 200 * 4);
        }
        finally { pinned.Free(); }
        for (int x = 60; x < 140; x++)   // away from the blur's edge fade
        {
            var (r, g, b, a) = (data[x * 4 + 2], data[x * 4 + 1], data[x * 4], data[x * 4 + 3]);
            Assert.True(a >= 250);          // the plate is opaque away from the picture's edge
            Assert.InRange(r, 238, 252);    // ~245, the light base over blurred white
            Assert.Equal(r, g);
            Assert.Equal(g, b);
        }
    }

    [Theory]
    [InlineData(WallpaperMode.Fill)]
    [InlineData(WallpaperMode.Fit)]
    [InlineData(WallpaperMode.Stretch)]
    [InlineData(WallpaperMode.Center)]
    [InlineData(WallpaperMode.Tile)]
    public void ScreenToWallpaper_maps_like_the_desktop_background(WallpaperMode mode)
    {
        // a 200x100 wallpaper on a 200x200 screen; the top-left 50x50 was sampled
        var crop = WallpaperMica.ScreenToWallpaper(new Rect(0, 0, 50, 50), new Size(200, 100), new Size(200, 200), mode);
        switch (mode)
        {
            case WallpaperMode.Fill:   // s = max(200/200, 200/100)=2 -> shown 400x200, ox=-100 -> x0=50; 50 screen px = 25 wall px
                Assert.Equal(50, crop.X, 1); Assert.Equal(25, crop.Width, 1);
                Assert.Equal(0, crop.Y, 1);  Assert.Equal(25, crop.Height, 1);
                break;
            case WallpaperMode.Fit:    // s = min(1, 2)=1 -> shown 200x100 centred, oy=50 -> y0=-50 (above the picture)
                Assert.Equal(0, crop.X, 1); Assert.Equal(50, crop.Width, 1);
                Assert.Equal(-50, crop.Y, 1); Assert.Equal(50, crop.Height, 1);
                break;
            case WallpaperMode.Stretch: // sy = 200/100 = 2 -> y0 = 0, 50 screen px = 25 wall px
                Assert.Equal(0, crop.X, 1); Assert.Equal(50, crop.Width, 1);
                Assert.Equal(0, crop.Y, 1); Assert.Equal(25, crop.Height, 1);
                break;
            case WallpaperMode.Center:  // oy = (200-100)/2 = 50 -> y0=-50 (above the picture)
                Assert.Equal(0, crop.X, 1); Assert.Equal(50, crop.Width, 1);
                Assert.Equal(-50, crop.Y, 1); Assert.Equal(50, crop.Height, 1);
                break;
            case WallpaperMode.Tile:    // same first tile, position modded into the picture
                Assert.Equal(0, crop.X, 1);
                Assert.Equal(50, crop.Width, 1);
                break;
        }
    }

    [AvaloniaFact]
    public void Desktop_labels_are_free_text_on_a_shadow()
    {
        var desktop = Path.Combine(Path.GetTempPath(), "vde-mica-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(desktop, "red.txt"), "hello");
        using var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Root };
        var w = new VExplore.Windows.DesktopWindow(desktop, hive, _ => { }) { Width = 800, Height = 600 };
        w.Show();
        try
        {
            Assert.True(w.IconsVisible);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var labels = FindLabels(w);
            Assert.NotEmpty(labels);
            foreach (var label in labels)
            {
                Assert.IsType<Avalonia.Media.DropShadowEffect>(label.Effect);   // the soft shadow
                Assert.Equal(Avalonia.Media.Brushes.White, label.Fill);          // white, whatever the theme, like Plasma's desktop
                Assert.Equal(2, label.MaxLines);
            }
        }
        finally
        {
            w.Close();
            Directory.Delete(desktop, true);
        }
    }

    private static System.Collections.Generic.List<Vde.Shell.Controls.OutlinedLabel> FindLabels(Avalonia.Controls.Window w)
    {
        var found = new System.Collections.Generic.List<Vde.Shell.Controls.OutlinedLabel>();
        void Walk(Visual v)
        {
            foreach (var child in v.GetVisualChildren())
            {
                if (child is Vde.Shell.Controls.OutlinedLabel t && t.Text == "red.txt")
                    found.Add(t);
                Walk((Visual)child);
            }
        }
        Walk(w);
        return found;
    }
}
