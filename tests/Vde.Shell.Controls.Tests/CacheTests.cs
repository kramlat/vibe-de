using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Vde.Freedesktop;
using Vde.Shell.Controls;

namespace Vde.Shell.Controls.Tests;

public class IconCacheTests
{
    private static WriteableBitmap Solid(int side, uint bgra)
    {
        var bmp = new WriteableBitmap(new PixelSize(side, side), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = bmp.Lock();
        for (int i = 0; i < side * side; i++)
            System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, i * 4, unchecked((int)bgra));
        return bmp;
    }

    [AvaloniaFact]
    public void The_disk_cache_gives_back_what_was_put_in_and_a_new_stamp_starts_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "vde-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new IconDiskCache("stamp-1", root);
            Assert.Null(cache.TryGet("folder|16x1"));

            cache.Put("folder|16x1", Solid(16, 0xFF0000FF));
            var back = cache.TryGet("folder|16x1");
            Assert.NotNull(back);
            Assert.Equal(new PixelSize(16, 16), back!.PixelSize);

            Assert.Null(new IconDiskCache("stamp-2", root).TryGet("folder|16x1"));   // the themes changed: the old picture is not used
            Assert.NotNull(new IconDiskCache("stamp-1", root).TryGet("folder|16x1"));  // another process with the same themes finds it
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void A_damaged_cache_file_is_just_a_miss()
    {
        var root = Path.Combine(Path.GetTempPath(), "vde-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new IconDiskCache("s", root);
            cache.Put("k", Solid(8, 0xFF00FF00));
            foreach (var file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
                File.WriteAllText(file, "not a png");
            Assert.Null(cache.TryGet("k"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void The_provider_shares_one_picture_per_ask_reads_the_cache_and_makes_icons_on_a_worker()
    {
        using var fx = new IconFixture();
        var provider = fx.Provider();
        Assert.False(provider.TryGetReady(["red"], 16, 1, out _));      // nothing made yet

        var made = provider.LoadAsync(["red"], 16).GetAwaiter().GetResult();
        Assert.NotNull(made);
        Assert.True(provider.TryGetReady(["red"], 16, 1, out var ready));
        Assert.Same(made, ready);                                        // asked again: the same picture, no work

        var again = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.LoadAsync(["blue"], 16))).GetAwaiter().GetResult();
        Assert.All(again, i => Assert.Same(again[0], i));                // eight asks at once share one
    }

    [AvaloniaFact]
    public void An_svg_is_rasterized_at_the_pixel_size_asked_for_not_always_large()
    {
        using var fx = new IconFixture();
        var file = Path.Combine(fx.Root, "data", "icons", "hicolor", "16", "red.svg");
        Assert.Equal(new PixelSize(22, 22), SvgRasterizer.Render(file, 22)!.PixelSize);
        Assert.Equal(new PixelSize(64, 64), SvgRasterizer.Render(file, 64)!.PixelSize);
        Assert.Equal(new PixelSize(256, 256), SvgRasterizer.Render(file)!.PixelSize);
    }

    [Fact]
    public void The_theme_stamp_changes_when_a_theme_is_touched()
    {
        using var fx = new IconFixture();
        var before = fx.Lookup.Stamp("hicolor");
        Assert.Equal(before, fx.Lookup.Stamp("hicolor"));
        File.SetLastWriteTimeUtc(Path.Combine(fx.Root, "data", "icons", "hicolor", "index.theme"), DateTime.UtcNow.AddHours(1));
        Assert.NotEqual(before, fx.Lookup.Stamp("hicolor"));
    }
}

public class WallpaperMicaCacheTests
{
    private static string Picture(string folder, int w, int h)
    {
        var path = Path.Combine(folder, "wall.png");
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(new SKColor(30, 90, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void A_wallpaper_is_made_once_kept_on_disk_and_found_there_by_the_next_program()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vde-mica-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(folder, "cache"));
        try
        {
            var path = Picture(folder, 640, 360);
            Assert.Null(WallpaperMica.Peek(path, 320));                       // not made yet: nothing slow happens here

            var done = new ManualResetEventSlim();
            WallpaperMica.WhenReady(path, done.Set, 320);                     // made on a worker
            Assert.True(done.Wait(TimeSpan.FromSeconds(20)));

            var made = WallpaperMica.Peek(path, 320);
            Assert.NotNull(made);
            Assert.Equal(320, made!.PixelSize.Width);
            Assert.True(Directory.EnumerateFiles(Path.Combine(folder, "cache"), "*.png", SearchOption.AllDirectories).Any());   // and kept for the next program

            Assert.Same(made, WallpaperMica.Source(path, 320));               // the complete call agrees
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void A_wallpaper_that_cannot_be_read_never_calls_back_and_is_not_retried()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vde-mica-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var bad = Path.Combine(folder, "bad.png");
        File.WriteAllText(bad, "not an image");
        try
        {
            var calls = 0;
            WallpaperMica.WhenReady(bad, () => Interlocked.Increment(ref calls), 320);
            Thread.Sleep(1500);
            WallpaperMica.WhenReady(bad, () => Interlocked.Increment(ref calls), 320);   // known bad: ignored at once
            Thread.Sleep(300);
            Assert.Equal(0, calls);
            Assert.Null(WallpaperMica.Peek(bad, 320));
        }
        finally { Directory.Delete(folder, true); }
    }
}
