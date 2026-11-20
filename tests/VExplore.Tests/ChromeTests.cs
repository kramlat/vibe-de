using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vde.Compositor;

namespace VExplore.Tests;

public class ChromeTests
{
    private static ChromeRequest Request(uint id = 1, bool active = true, string hover = "none", bool pressed = false, bool maximized = false, int width = 600)
        => new(id, width, active, maximized, hover, pressed, "Untitled - Notes", "org.kde.kate");

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] px, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    [AvaloniaFact]
    public void The_titlebar_is_the_requested_width_and_32_high_with_rounded_transparent_top_corners()
    {
        var renderer = new ChromeRenderer();
        var px = renderer.Render(Request());

        Assert.Equal(600 * 32 * 4, px.Length);
        Assert.True(Pixel(px, 600, 0, 0).A < 255);       // the rounded corner lets the desktop show
        Assert.Equal(255, Pixel(px, 600, 300, 16).A);    // the bar itself is opaque
    }

    [AvaloniaFact]
    public void Hovering_or_pressing_the_close_button_and_going_inactive_change_the_drawing()
    {
        var renderer = new ChromeRenderer();
        var plain = renderer.Render(Request());
        var overClose = renderer.Render(Request(hover: "close"));
        var pressedClose = renderer.Render(Request(hover: "close", pressed: true));
        var inactive = renderer.Render(Request(active: false));
        var restored = renderer.Render(Request(maximized: true));

        const int closeX = 600 - 23, y = 16;   // the middle of the close button, 46 px wide at the right end
        Assert.NotEqual(Pixel(plain, 600, closeX - 20, y), Pixel(overClose, 600, closeX - 20, y));   // its fill appears
        Assert.NotEqual(Pixel(overClose, 600, closeX - 20, y), Pixel(pressedClose, 600, closeX - 20, y));
        Assert.NotEqual(plain, inactive);
        Assert.NotEqual(plain, restored);   // the maximize glyph becomes "restore"
        var minX = 600 - 3 * 46 + 5;
        Assert.Equal(Pixel(plain, 600, minX, 4), Pixel(overClose, 600, minX, 4));   // other buttons stay as they were
    }

    [AvaloniaFact]
    public void The_same_request_always_draws_the_same_pixels_and_a_narrow_window_still_renders()
    {
        var renderer = new ChromeRenderer();
        Assert.Equal(renderer.Render(Request()), renderer.Render(Request()));
        Assert.Equal(160 * 32 * 4, renderer.Render(Request(width: 160)).Length);
    }

    [AvaloniaFact]
    public void Titlebar_pictures()
    {
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is not { Length: > 0 } dir) return;
        var renderer = new ChromeRenderer();
        foreach (var (name, request) in new[] { ("active", Request()), ("hover-close", Request(hover: "close")), ("inactive", Request(active: false)), ("maximized", Request(maximized: true, hover: "max")) })
        {
            var px = renderer.Render(request);
            using var bmp = new WriteableBitmap(new PixelSize(600, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock())
                System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
            bmp.Save(Path.Combine(dir, $"vwm-titlebar-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    [AvaloniaFact]
    public void The_window_menu_is_drawn_at_the_agreed_size_and_follows_hover_and_enabled_rows()
    {
        var renderer = new ChromeRenderer();
        var all = renderer.RenderMenu(new MenuRequest(1, 0b111111, -1, false));
        var hoverMove = renderer.RenderMenu(new MenuRequest(1, 0b111111, 1, false));
        var moveDisabled = renderer.RenderMenu(new MenuRequest(1, 0b111101, 1, true));

        Assert.Equal(220 * 188 * 4, all.Length);   // MENU_W x MENU_HEIGHT in native/vwm/vwm.h
        Assert.NotEqual(all, hoverMove);
        Assert.NotEqual(hoverMove, moveDisabled);   // a disabled row gets no hover fill and is dimmed
        Assert.Equal(255, Pixel(all, 220, 110, 94).A);

        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is { Length: > 0 } dir)
            foreach (var (name, px) in new[] { ("plain", all), ("hover-move", hoverMove), ("maximized", moveDisabled) })
            {
                using var bmp = new WriteableBitmap(new PixelSize(220, 188), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bmp.Lock())
                    System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
                bmp.Save(Path.Combine(dir, $"vwm-menu-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
    }
}
