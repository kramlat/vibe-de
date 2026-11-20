using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The graph of a file transfer, drawn the way Windows draws it: a green grid, the part of the job that is done
// shaded light, the speed so far as a dark green area over it, and the current speed written in the corner.
// The horizontal axis is how far along the job is (not time); the vertical axis scales itself to the fastest
// point. Also usable for any "value against progress" curve.
public sealed class SpeedGraphView : Control
{
    private IReadOnlyList<(double X, double Y)> _points = [];
    private double _progress;
    private string _caption = "";
    private double _maximum;

    static SpeedGraphView() { }

    public SpeedGraphView() => ClipToBounds = true;

    // X in 0..1 (progress when the value was taken), Y the value; the points are expected in order.
    public IReadOnlyList<(double X, double Y)> Points { get => _points; set { _points = value; InvalidateVisual(); } }
    public double Progress { get => _progress; set { _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    // 0 scales the curve to its highest point; anything above fixes the top of the graph (100 for a percentage)
    public double Maximum { get => _maximum; set { _maximum = value; InvalidateVisual(); } }
    public string Caption { get => _caption; set { _caption = value; InvalidateVisual(); } }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 84);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeTokens.Changed += OnTheme;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeTokens.Changed -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 4 || h < 4) return;
        var surface = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.White);
        var text = ThemeTokens.Find(this, ThemeToken.TextPrimary, Brushes.Black);
        var green = Color.Parse("#00A82D");
        var gridPen = new Pen(new SolidColorBrush(green, 0.28), 1);
        var frame = new Rect(0.5, 0.5, w - 1, h - 1);

        context.DrawRectangle(surface, null, frame);
        // the grid: ten columns, four rows
        for (int i = 1; i < 10; i++)
            context.DrawLine(gridPen, new Point(Math.Round(w * i / 10) + 0.5, 0), new Point(Math.Round(w * i / 10) + 0.5, h));
        for (int i = 1; i < 4; i++)
            context.DrawLine(gridPen, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));

        // what is done so far is shaded
        if (_progress > 0)
            context.DrawRectangle(new SolidColorBrush(green, 0.30), null, new Rect(0, 0, w * _progress, h));

        // the speed curve, scaled to its fastest point with a little head room
        double peak = _points.Count == 0 ? 0 : _points.Max(p => p.Y);
        double scale = _maximum > 0 ? (h - 8) / _maximum : peak <= 0 ? 0 : (h - 8) / (peak * 1.1);
        double floor = h - 3;
        if (_points.Count > 1 && scale > 0)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(_points[0].X * w, floor), isFilled: true);
                foreach (var (x, y) in _points)
                    g.LineTo(new Point(x * w, floor - y * scale));
                g.LineTo(new Point(_points[^1].X * w, floor));
                g.EndFigure(isClosed: true);
            }
            context.DrawGeometry(new SolidColorBrush(green, 0.88), new Pen(new SolidColorBrush(Color.Parse("#007A20")), 1), geometry);
        }

        // the baseline and, under it, a green strip as long as the job is done
        context.DrawLine(new Pen(text, 1), new Point(0, floor + 0.5), new Point(w, floor + 0.5));
        context.DrawRectangle(new SolidColorBrush(green), null, new Rect(0, h - 2, w * _progress, 2));

        if (_caption.Length > 0)
        {
            var label = new FormattedText(_caption, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, text);
            context.DrawText(label, new Point(w - label.Width - 6, h - label.Height - 6));
        }
        context.DrawRectangle(null, new Pen(new SolidColorBrush(green, 0.55), 1), frame);
    }
}
