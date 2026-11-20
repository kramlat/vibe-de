using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Vde.Shell.Controls;

// A label that stays readable on any picture, for text drawn straight on the wallpaper (desktop icon names).
// Plasma's desktop draws such labels in white on a dark shadow whatever the theme; here the letters are also
// outlined: the glyph outlines are stroked in dark first, then filled, so every letter has an edge against light
// and busy backgrounds and the shadow only has to soften it. Wraps to MaxLines lines, centred, and ends a
// longer name with an ellipsis.
public sealed class OutlinedLabel : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<OutlinedLabel, string?>(nameof(Text));
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<OutlinedLabel, double>(nameof(FontSize), 13);
    public static readonly StyledProperty<int> MaxLinesProperty = AvaloniaProperty.Register<OutlinedLabel, int>(nameof(MaxLines), 2);
    public static readonly StyledProperty<IBrush> FillProperty = AvaloniaProperty.Register<OutlinedLabel, IBrush>(nameof(Fill), Brushes.White);
    public static readonly StyledProperty<IBrush> OutlineProperty = AvaloniaProperty.Register<OutlinedLabel, IBrush>(nameof(Outline), new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    public static readonly StyledProperty<bool> ItalicProperty = AvaloniaProperty.Register<OutlinedLabel, bool>(nameof(Italic));

    static OutlinedLabel()
    {
        AffectsMeasure<OutlinedLabel>(TextProperty, FontSizeProperty, MaxLinesProperty);
        AffectsRender<OutlinedLabel>(TextProperty, FontSizeProperty, MaxLinesProperty, FillProperty, OutlineProperty, ItalicProperty);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public int MaxLines { get => GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }
    public IBrush Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public bool Italic { get => GetValue(ItalicProperty); set => SetValue(ItalicProperty, value); }

    private FormattedText? Layout(double width)
    {
        if (string.IsNullOrEmpty(Text) || double.IsNaN(width) || width < 1) return null;
        var typeface = new Typeface(FontFamily.Default, Italic ? FontStyle.Italic : FontStyle.Normal);
        return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Fill)
        {
            MaxTextWidth = width,
            MaxLineCount = Math.Max(1, MaxLines),
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        var f = Layout(width);
        return f is null ? new Size(width, 0) : new Size(width, Math.Ceiling(f.Height));
    }

    public override void Render(DrawingContext context)
    {
        var f = Layout(Bounds.Width);
        if (f is null) return;
        var origin = new Point(0, 0);
        // the glyph outlines, stroked wide, are the edge; the fill on top leaves a rim of it showing
        using (context.PushOpacity(1))
            if (f.BuildGeometry(origin) is { } outline)
                context.DrawGeometry(null, new Pen(Outline, 3, lineJoin: PenLineJoin.Round), outline);
        context.DrawText(f, origin);
    }
}
