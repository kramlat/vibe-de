using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

public enum CaptionKind { Minimize, Maximize, Restore, Close }

// The one titlebar button every window uses (the compositor's titlebars, the file manager, the terminal), so
// they look and behave the same: a symbol drawn with lines (no font needed), a fill under the pointer, and
// always red under the pointer for Close. In the Redmond style it is a classic raised grey button instead, with
// no hover colour at all (only the pressed look).
public sealed class CaptionButton : Control
{
    // The red of a hot Close button, and the lighter one while it is pressed.
    public static readonly Color CloseHot = Color.Parse("#E81123");
    public static readonly Color ClosePressed = Color.Parse("#F1707A");

    private bool _hot, _pressed;

    public CaptionButton(CaptionKind kind = CaptionKind.Close)
    {
        Kind = kind;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        Width = 46;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
    }

    public CaptionKind Kind { get; set; }
    // False while the window is not the active one: the symbol is dimmer, as the title is.
    public bool WindowActive { get; set; } = true;
    // A smaller button for tab strips: a hot fill with rounded corners.
    public bool Compact { get; set; }

    public bool IsHot { get => _hot; set { _hot = value; InvalidateVisual(); } }
    public bool IsPressed { get => _pressed; set { _pressed = value; InvalidateVisual(); } }

    public event EventHandler? Click;

    // The fill drawn for a hot (or pressed) button; null when it has none. The red for Close, whatever the theme.
    public static IBrush? HotFill(Control owner, CaptionKind kind, bool pressed)
        => kind == CaptionKind.Close
            ? new SolidColorBrush(pressed ? ClosePressed : CloseHot)
            : new SolidColorBrush((ThemeTokens.Find(owner, ThemeToken.HoverFill, Brushes.Gray) as ISolidColorBrush)?.Color ?? Colors.Gray, pressed ? 0.8 : 0.5);

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); IsHot = true; }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); IsHot = false; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        IsPressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;   // the titlebar under it must not start a window drag
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsPressed) return;
        IsPressed = false;
        e.Pointer.Capture(null);
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            Click?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));   // a control draws nothing hit-testable by itself: without this the pointer would pass through the idle button
        Draw(context, new Rect(Bounds.Size), Kind, _hot, _pressed && _hot, WindowActive, this, Compact);
    }

    // Draws one button into bounds; the compositor's titlebar images use this too (the pointer state comes from vwm there).
    public static void Draw(DrawingContext context, Rect bounds, CaptionKind kind, bool hot, bool pressed, bool active, Control themeSource, bool compact = false)
    {
        var classic = ThemeTokens.IsClassic(themeSource);
        var close = kind == CaptionKind.Close;
        IBrush ink = active || hot ? ThemeTokens.Find(themeSource, ThemeToken.TextPrimary, Brushes.White) : ThemeTokens.Find(themeSource, ThemeToken.TextSecondary, Brushes.Gray);

        if (classic)
            DrawClassicFace(context, bounds, pressed, themeSource, ref ink);
        else if (hot || pressed)
        {
            if (HotFill(themeSource, kind, pressed) is { } fill)
                context.DrawRectangle(fill, null, compact ? bounds.Deflate(2) : bounds, compact ? 4 : 0, compact ? 4 : 0);
            if (close) ink = Brushes.White;
        }

        var pen = new Pen(ink, 1);
        var c = bounds.Center;
        if (classic && pressed) c = new Point(c.X + 1, c.Y + 1);
        switch (kind)
        {
            case CaptionKind.Minimize:
                context.DrawLine(pen, new Point(c.X - 4, c.Y + 3.5), new Point(c.X + 4, c.Y + 3.5));
                if (classic) context.DrawLine(pen, new Point(c.X - 4, c.Y + 4.5), new Point(c.X + 4, c.Y + 4.5));
                break;
            case CaptionKind.Maximize:
                context.DrawRectangle(null, pen, new Rect(c.X - 4.5, c.Y - 4.5, 9, 9));
                if (classic) context.DrawLine(pen, new Point(c.X - 4.5, c.Y - 3.5), new Point(c.X + 4.5, c.Y - 3.5));   // the heavy top edge
                break;
            case CaptionKind.Restore:   // two overlapping squares
                context.DrawRectangle(null, pen, new Rect(c.X - 4.5, c.Y - 1.5, 7, 6));
                context.DrawLine(pen, new Point(c.X - 2.5, c.Y - 1.5), new Point(c.X - 2.5, c.Y - 4.5));
                context.DrawLine(pen, new Point(c.X - 2.5, c.Y - 4.5), new Point(c.X + 4.5, c.Y - 4.5));
                context.DrawLine(pen, new Point(c.X + 4.5, c.Y - 4.5), new Point(c.X + 4.5, c.Y + 1.5));
                context.DrawLine(pen, new Point(c.X + 4.5, c.Y + 1.5), new Point(c.X + 2.5, c.Y + 1.5));
                break;
            default:
                var cross = new Pen(ink, classic ? 1.6 : 1);
                context.DrawLine(cross, new Point(c.X - 4, c.Y - 4), new Point(c.X + 4, c.Y + 4));
                context.DrawLine(cross, new Point(c.X - 4, c.Y + 4), new Point(c.X + 4, c.Y - 4));
                break;
        }
    }

    // The classic button: a raised grey square with a white top and left edge and a dark bottom and right one;
    // pressed, the edges swap. It has no hover colour at all, Close included: nothing changes under the pointer.
    private static void DrawClassicFace(DrawingContext context, Rect bounds, bool pressed, Control themeSource, ref IBrush ink)
    {
        var face = new Rect(bounds.Center.X - 9, bounds.Center.Y - 8, 18, 16);
        context.DrawRectangle(ThemeTokens.Find(themeSource, ThemeToken.Surface, new SolidColorBrush(Color.Parse("#D4D0C8"))), null, face);   // the style's own face grey
        var light = new Pen(Brushes.White, 1);
        var dark = new Pen(new SolidColorBrush(Color.Parse("#404040")), 1);
        var mid = new Pen(new SolidColorBrush(Color.Parse("#808080")), 1);
        var (tl, br) = pressed ? (dark, light) : (light, dark);
        context.DrawLine(tl, new Point(face.Left + 0.5, face.Bottom - 1), new Point(face.Left + 0.5, face.Top + 0.5));
        context.DrawLine(tl, new Point(face.Left + 0.5, face.Top + 0.5), new Point(face.Right - 1, face.Top + 0.5));
        context.DrawLine(br, new Point(face.Right - 0.5, face.Top), new Point(face.Right - 0.5, face.Bottom - 0.5));
        context.DrawLine(br, new Point(face.Left, face.Bottom - 0.5), new Point(face.Right - 0.5, face.Bottom - 0.5));
        context.DrawLine(pressed ? mid : mid, new Point(face.Right - 1.5, face.Top + 1), new Point(face.Right - 1.5, face.Bottom - 1.5));
        context.DrawLine(mid, new Point(face.Left + 1, face.Bottom - 1.5), new Point(face.Right - 1.5, face.Bottom - 1.5));
        ink = Brushes.Black;
    }
}
