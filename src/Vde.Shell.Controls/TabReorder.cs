using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Vde.Shell.Controls;

// Drag a tab header sideways to reorder it within its strip. The strip's children move live as the pointer
// crosses their midpoints; the owner is told each move so it can keep its own tab list in the same order.
public static class TabReorder
{
    private const double Threshold = 6;

    public static void Attach(Panel strip, Control header, Action<int, int> moved)
    {
        Point? start = null;
        var dragging = false;

        header.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            // a press on the close button is not the start of a drag
            if (e.Source is Visual v && v.FindAncestorOfType<CaptionButton>(true) is not null) return;
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(strip);
            dragging = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        header.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (start is not { } from) return;
            var now = e.GetPosition(strip);
            if (!dragging)
            {
                if (Math.Abs(now.X - from.X) < Threshold) return;
                dragging = true;
                e.Pointer.Capture(header);
            }
            Move(strip, header, now.X, moved);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        void End(object? _, PointerEventArgs e)
        {
            if (dragging) e.Pointer.Capture(null);
            start = null;
        }
        header.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            var was = dragging;
            End(s, e);
            if (was) e.Handled = true;   // a drag is not a click
            dragging = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        header.PointerCaptureLost += (_, _) => { start = null; dragging = false; };
    }

    // Moves the header to the slot the pointer is over.
    internal static void Move(Panel strip, Control header, double x, Action<int, int> moved)
    {
        var from = strip.Children.IndexOf(header);
        if (from < 0) return;
        var to = from;
        for (var i = 0; i < strip.Children.Count; i++)
        {
            var child = strip.Children[i];
            if (child == header) continue;
            var mid = child.Bounds.X + child.Bounds.Width / 2;
            if (i < from && x < mid) { to = i; break; }
            if (i > from && x > mid) to = i;
        }
        if (to == from) return;
        strip.Children.RemoveAt(from);
        strip.Children.Insert(to, header);
        moved(from, to);
    }
}
