using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vde.Shell;

namespace Vde.Shell.Controls;

// What the lock screen looks like: the picture (or colour) with the time and date at the top and the chosen
// status app's line at the bottom. The Lock screen settings page shows it small as a preview; the lock screen
// window shows it full size. `scale` is the text size relative to a full-size screen.
public sealed class LockScreenView : UserControl
{
    private readonly Func<DateTime> _now;
    private readonly Border _background = new();
    private readonly TextBlock _time = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private readonly TextBlock _date = new() { HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Foreground = Brushes.White, IsVisible = false };
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Bitmap? _picture;
    private int _slide;
    private LockScreenSettings _settings = new();
    private DesktopSettings _desktop = new();

    public LockScreenView(double scale = 1, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        _time.FontSize = 96 * scale;
        _date.FontSize = 28 * scale;
        _status.FontSize = 16 * scale;
        _status.Margin = new Thickness(24 * scale);
        var shadow = new DropShadowEffect { BlurRadius = 8 * scale, OffsetX = 0, OffsetY = 1, Color = Colors.Black, Opacity = 0.6 };
        _time.Effect = shadow;
        _date.Effect = shadow;
        _status.Effect = shadow;

        var clock = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60 * scale, 0, 0), Children = { _time, _date } };
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(_background);
        root.Children.Add(clock);
        root.Children.Add(_status);
        Content = root;
        _timer.Tick += (_, _) => Tick();
        Tick();
    }

    public string TimeText => _time.Text ?? "";
    public string DateText => _date.Text ?? "";
    public string? StatusText => _status.IsVisible ? _status.Text : null;

    // Shows the picture or colour the settings call for; statusLine is the status app's one-line status, if any.
    public void Apply(LockScreenSettings settings, DesktopSettings desktop, string? statusLine = null)
    {
        _settings = settings;
        _desktop = desktop;
        _status.Text = statusLine;
        _status.IsVisible = !string.IsNullOrEmpty(statusLine);
        ShowBackground();
    }

    // Moves a slideshow on to its next picture.
    public void NextSlide()
    {
        _slide++;
        ShowBackground();
    }

    private void ShowBackground()
    {
        _picture?.Dispose();
        _picture = null;
        var color = Color.TryParse(_settings.Color, out var c) ? c : Color.Parse("#1E1E2E");
        var path = LockScreenSettingsStore.PictureToShow(_settings, _desktop, _slide);
        if (path is not null)
            try { _picture = new Bitmap(path); }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { }
        _background.Background = _picture is not null
            ? new ImageBrush(_picture) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(_settings.Background == LockBackground.Color ? color : Color.Parse("#1E1E2E"));
    }

    private void Tick()
    {
        var now = _now();
        _time.Text = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture).Replace(" ", "");
        _date.Text = now.ToString("dddd, MMMM d", System.Globalization.CultureInfo.CurrentCulture);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }
}
