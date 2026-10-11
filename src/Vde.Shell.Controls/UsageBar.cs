using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// A thin rounded bar showing how full something is. Turns critical-coloured when nearly full.
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Value));

    // From this fraction up the bar uses the critical colour (Explorer does this at 90 %).
    public static readonly StyledProperty<double> WarnAtProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(WarnAt), 0.9);

    static UsageBar()
    {
        AffectsRender<UsageBar>(ValueProperty, WarnAtProperty);
        AffectsMeasure<UsageBar>();
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double WarnAt
    {
        get => GetValue(WarnAtProperty);
        set => SetValue(WarnAtProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, 6);

    public override void Render(DrawingContext context)
    {
        var track = new Rect(0, 0, Bounds.Width, Bounds.Height);
        double radius = Bounds.Height / 2;
        context.DrawRectangle(Token(ThemeToken.Track, Brushes.Gray), null, track, radius, radius);

        double fraction = Math.Clamp(double.IsNaN(Value) ? 0 : Value, 0, 1);
        if (fraction <= 0)
            return;

        var fill = fraction >= WarnAt
            ? Token(ThemeToken.Critical, Brushes.Crimson)
            : Token(ThemeToken.Accent, Brushes.RoyalBlue);
        // Never narrower than the corner radius, or the fill degenerates into a sliver.
        double width = Math.Max(Bounds.Width * fraction, Bounds.Height);
        context.DrawRectangle(fill, null, new Rect(0, 0, width, Bounds.Height), radius, radius);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeTokens.Changed += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeTokens.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private IBrush Token(ThemeToken token, IBrush fallback) => ThemeTokens.Find(this, token, fallback);
}
