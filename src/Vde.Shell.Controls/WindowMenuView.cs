using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// The menu a window's icon (or right-click on its titlebar) opens: Restore, Move, Size, Minimize, Maximize, Close.
// Like the titlebar, vwm positions the image and handles the pointer, so the sizes are a contract with
// native/vwm/vwm.h (MENU_W, MENU_ROW_H, MENU_PAD, six rows in this order).
public sealed class WindowMenuView : Control
{
    public const int Width_ = 220, RowHeight = 30, Padding_ = 4;
    public static readonly string[] Labels = ["Restore", "Move", "Size", "Minimize", "Maximize", "Close"];
    public static readonly string?[] Hints = [null, null, null, null, null, "Alt+F4"];

    public const int Rows = 6;
    public const int Height_ = Rows * RowHeight + 2 * Padding_;

    private uint _enabled = 0b111111;
    private int _hover = -1;

    public WindowMenuView()
    {
        Width = Width_;
        Height = Height_;
        ClipToBounds = true;
    }

    // One bit per row, in row order; a cleared bit is drawn dimmed and does nothing.
    public uint Enabled { get => _enabled; set { _enabled = value; InvalidateVisual(); } }
    public int Hover { get => _hover; set { _hover = value; InvalidateVisual(); } }

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
        var surface = ThemeTokens.Find(this, ThemeToken.Surface, new SolidColorBrush(Color.Parse("#202030")));
        var border = ThemeTokens.Find(this, ThemeToken.Border, Brushes.Gray);
        var primary = ThemeTokens.Find(this, ThemeToken.TextPrimary, Brushes.White);
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        var hoverFill = ThemeTokens.Find(this, ThemeToken.HoverFill, Brushes.DimGray);
        var critical = ThemeTokens.Find(this, ThemeToken.Critical, Brushes.Crimson);
        context.DrawRectangle(surface, new Pen(border, 1), new Rect(0.5, 0.5, Width_ - 1, Height_ - 1), 8, 8);

        // a hairline between the window-state rows and Close, like the Windows menu has
        var sepY = Padding_ + 5 * RowHeight;
        context.DrawLine(new Pen(border, 1), new Point(Padding_ + 8, sepY + 0.5), new Point(Width_ - Padding_ - 8, sepY + 0.5));

        for (var i = 0; i < Labels.Length; i++)
        {
            var enabled = (_enabled & (1u << i)) != 0;
            var row = new Rect(Padding_, Padding_ + i * RowHeight, Width_ - 2 * Padding_, RowHeight);
            var hot = i == _hover && enabled;
            if (hot)
                context.DrawRectangle(i == 5 ? critical : hoverFill, null, row.Deflate(new Thickness(0, 1)), 4, 4);
            var ink = !enabled ? secondary : hot && i == 5 ? Brushes.White : primary;
            DrawGlyph(context, i, new Point(row.X + 20, row.Center.Y), new Pen(ink, 1, lineCap: PenLineCap.Round));
            var label = new FormattedText(Labels[i], System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, ink);
            context.DrawText(label, new Point(row.X + 40, row.Y + (RowHeight - label.Height) / 2));
            if (Hints[i] is { } hint)
            {
                var h = new FormattedText(hint, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 11, hot && i == 5 ? Brushes.White : secondary);
                context.DrawText(h, new Point(row.Right - 12 - h.Width, row.Y + (RowHeight - h.Height) / 2));
            }
        }
    }

    // The same symbols the titlebar buttons use, plus arrows for Move and Size.
    private static void DrawGlyph(DrawingContext c, int row, Point m, Pen pen)
    {
        switch (row)
        {
            case 0:   // restore
                c.DrawRectangle(null, pen, new Rect(m.X - 5, m.Y - 2, 7, 7));
                c.DrawLine(pen, new Point(m.X - 3, m.Y - 2), new Point(m.X - 3, m.Y - 5));
                c.DrawLine(pen, new Point(m.X - 3, m.Y - 5), new Point(m.X + 5, m.Y - 5));
                c.DrawLine(pen, new Point(m.X + 5, m.Y - 5), new Point(m.X + 5, m.Y + 3));
                c.DrawLine(pen, new Point(m.X + 5, m.Y + 3), new Point(m.X + 2, m.Y + 3));
                break;
            case 1:   // move: a cross of arrows
                c.DrawLine(pen, new Point(m.X - 6, m.Y), new Point(m.X + 6, m.Y));
                c.DrawLine(pen, new Point(m.X, m.Y - 6), new Point(m.X, m.Y + 6));
                foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    var tip = new Point(m.X + dx * 6, m.Y + dy * 6);
                    c.DrawLine(pen, tip, new Point(tip.X - dx * 2 + dy * 2, tip.Y - dy * 2 + dx * 2));
                    c.DrawLine(pen, tip, new Point(tip.X - dx * 2 - dy * 2, tip.Y - dy * 2 - dx * 2));
                }
                break;
            case 2:   // size: a diagonal arrow
                c.DrawLine(pen, new Point(m.X - 5, m.Y + 5), new Point(m.X + 5, m.Y - 5));
                c.DrawLine(pen, new Point(m.X + 5, m.Y - 5), new Point(m.X + 1, m.Y - 5));
                c.DrawLine(pen, new Point(m.X + 5, m.Y - 5), new Point(m.X + 5, m.Y - 1));
                c.DrawLine(pen, new Point(m.X - 5, m.Y + 5), new Point(m.X - 1, m.Y + 5));
                c.DrawLine(pen, new Point(m.X - 5, m.Y + 5), new Point(m.X - 5, m.Y + 1));
                break;
            case 3:   // minimize
                c.DrawLine(pen, new Point(m.X - 5, m.Y + 0.5), new Point(m.X + 5, m.Y + 0.5));
                break;
            case 4:   // maximize
                c.DrawRectangle(null, pen, new Rect(m.X - 5, m.Y - 5, 10, 10));
                break;
            default:  // close
                c.DrawLine(pen, new Point(m.X - 5, m.Y - 5), new Point(m.X + 5, m.Y + 5));
                c.DrawLine(pen, new Point(m.X - 5, m.Y + 5), new Point(m.X + 5, m.Y - 5));
                break;
        }
    }
}
