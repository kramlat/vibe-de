using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Vde.Shell.Controls;

// Renders a mock taskbar with real VDE controls offscreen and shows it as a layer-shell panel at the top edge.
// usage: PanelSpike <libvdepanel.so> [milliseconds] [png-to-write]
var library = args[0];
int millis = args.Length > 1 ? int.Parse(args[1]) : 2500;

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

const int width = 760, height = 48;
var bar = new Border
{
    Background = new SolidColorBrush(Color.Parse("#E61E1E2E")),
    CornerRadius = new CornerRadius(0, 0, 12, 12),
    Child = new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(18, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new ShellIcon { IconName = "start-here", IconSize = 28 },
            new ShellIcon { IconName = "system-file-manager", IconSize = 28 },
            new ShellIcon { IconName = "preferences-system", IconSize = 28 },
            new TextBlock { Text = "Vibe Explorer panel spike: drawn by Avalonia, shown by wlr-layer-shell", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
        },
    },
};
var window = new Window { Width = width, Height = height, Content = bar, Background = Brushes.Transparent, SystemDecorations = SystemDecorations.None };
window.Show();
var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing was rendered");
if (args.Length > 2) frame.Save(args[2]);

var pixels = new byte[width * height * 4];
using (var fb = frame.Lock())
    for (int y = 0; y < height; y++)
        Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * width * 4, width * 4);
Console.WriteLine($"rendered {frame.PixelSize.Width}x{frame.PixelSize.Height}, format {frame.Format}");

// The wire format is premultiplied ARGB8888, which is BGRA byte order on little-endian machines. Skia may hand
// over RGBA and/or unpremultiplied pixels depending on the platform, so convert whatever it gave us.
bool rgba = frame.Format?.ToString() == "Rgba8888";
bool unpremul = frame.AlphaFormat == Avalonia.Platform.AlphaFormat.Unpremul;
for (int i = 0; i < pixels.Length; i += 4)
{
    byte r = pixels[i + (rgba ? 0 : 2)], g = pixels[i + 1], b = pixels[i + (rgba ? 2 : 0)], a = pixels[i + 3];
    if (unpremul) { r = (byte)(r * a / 255); g = (byte)(g * a / 255); b = (byte)(b * a / 255); }
    pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r;
}
Console.WriteLine($"converted from {frame.Format}, alpha {frame.AlphaFormat}");

[DllImport("libvdepanel.so", EntryPoint = "vdepanel_show")] static extern int Show(byte[] bgra, int width, int height, uint anchor, int exclusive, int millis);
[DllImport("libvdepanel.so", EntryPoint = "vdepanel_last_error")] static extern IntPtr LastError();

NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) => name == "libvdepanel.so" ? NativeLibrary.Load(library) : IntPtr.Zero);

Console.WriteLine($"showing a {width}x{height} panel at the top edge for {millis} ms (reserves no space)...");
int result = Show(pixels, width, height, anchor: 1 /* top */, exclusive: 0, millis);
Console.WriteLine(result == 0 ? "OK: the compositor accepted and displayed the panel" : "FAILED: " + Marshal.PtrToStringAnsi(LastError()));
return result;

class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
