using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

public enum ChromeButton { None, Minimize, Maximize, Close }

// A window's titlebar as the compositor (vwm) shows it: app icon, title and the minimize / maximize / close
// buttons, drawn with the shell's theme so every window looks like part of the desktop. vwm places the image and
// handles the pointer, so the sizes here are a contract with native/vwm/vwm.h: the bar is Height tall and the
// three buttons are ButtonWidth wide at the right end, in this order.
public sealed class WindowChromeView : Border
{
    public const int BarHeight = 32;
    public const int ButtonWidth = 46;

    private readonly ShellIcon _icon = new() { IconSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 8, 0) };
    private readonly Glyph[] _glyphs;
    private bool _active = true, _maximized;
    private ChromeButton _hover, _pressed;

    public WindowChromeView()
    {
        Height = BarHeight;
        _glyphs = [new Glyph(this, ChromeButton.Minimize), new Glyph(this, ChromeButton.Maximize), new Glyph(this, ChromeButton.Close)];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"Auto,*,{ButtonWidth * 3}") };
        grid.Children.Add(_icon);
        Grid.SetColumn(_title, 1);
        grid.Children.Add(_title);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ButtonWidth},{ButtonWidth},{ButtonWidth}") };
        for (var i = 0; i < 3; i++)
        {
            Grid.SetColumn(_glyphs[i], i);
            buttons.Children.Add(_glyphs[i]);
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        Child = grid;
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        ClipToBounds = true;
        Apply();
    }

    public string Title { get => _title.Text ?? ""; set { _title.Text = value; } }
    public IReadOnlyList<string>? IconNames
    {
        get => _icon.IconNames;
        set
        {
            _icon.IconNames = value;
            _icon.IsVisible = value is { Count: > 0 };
            _title.Margin = new Thickness(_icon.IsVisible ? 0 : 12, 0, 8, 0);   // with no icon the title takes its place
        }
    }
    public bool Active { get => _active; set { _active = value; Apply(); } }
    public bool Maximized { get => _maximized; set { _maximized = value; Apply(); } }
    public ChromeButton Hover { get => _hover; set { _hover = value; Apply(); } }
    public bool Pressed { get => _pressed != ChromeButton.None; set { _pressed = value ? _hover : ChromeButton.None; Apply(); } }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeTokens.Changed += OnTheme;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeTokens.Changed -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        Background = ThemeTokens.Find(this, ThemeToken.Surface, new SolidColorBrush(Color.Parse("#202030")));
        var primary = ThemeTokens.Find(this, ThemeToken.TextPrimary, Brushes.White);
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        _title.Foreground = _active ? primary : secondary;
        _icon.Opacity = _active ? 1 : 0.6;
        foreach (var g in _glyphs) g.InvalidateVisual();
    }

    // One button: a hover fill and a drawn symbol (no font needed, so it looks the same everywhere).
    private sealed class Glyph(WindowChromeView owner, ChromeButton kind) : Control
    {
        public override void Render(DrawingContext context)
        {
            var hovered = owner._hover == kind;
            var pressed = hovered && owner._pressed == kind;
            var close = kind == ChromeButton.Close;
            var bounds = new Rect(Bounds.Size);
            IBrush ink = owner._active || hovered ? ThemeTokens.Find(owner, ThemeToken.TextPrimary, Brushes.White) : ThemeTokens.Find(owner, ThemeToken.TextSecondary, Brushes.Gray);
            if (hovered)
            {
                var critical = ThemeTokens.Find(owner, ThemeToken.Critical, Brushes.Crimson);
                var fill = close ? critical : ThemeTokens.Find(owner, ThemeToken.HoverFill, Brushes.Gray);
                context.DrawRectangle(fill, null, bounds);
                if (pressed) context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.18), null, bounds);
                if (close) ink = Brushes.White;
            }
            var pen = new Pen(ink, 1);
            var c = bounds.Center;
            switch (kind)
            {
                case ChromeButton.Minimize:
                    context.DrawLine(pen, new Point(c.X - 5, c.Y + 0.5), new Point(c.X + 5, c.Y + 0.5));
                    break;
                case ChromeButton.Maximize when !owner._maximized:
                    context.DrawRectangle(null, pen, new Rect(c.X - 5, c.Y - 5, 10, 10));
                    break;
                case ChromeButton.Maximize:   // restore: two overlapping squares
                    context.DrawRectangle(null, pen, new Rect(c.X - 5, c.Y - 2, 7, 7));
                    context.DrawLine(pen, new Point(c.X - 3, c.Y - 2), new Point(c.X - 3, c.Y - 5));
                    context.DrawLine(pen, new Point(c.X - 3, c.Y - 5), new Point(c.X + 5, c.Y - 5));
                    context.DrawLine(pen, new Point(c.X + 5, c.Y - 5), new Point(c.X + 5, c.Y + 3));
                    context.DrawLine(pen, new Point(c.X + 5, c.Y + 3), new Point(c.X + 2, c.Y + 3));
                    break;
                default:
                    context.DrawLine(pen, new Point(c.X - 5, c.Y - 5), new Point(c.X + 5, c.Y + 5));
                    context.DrawLine(pen, new Point(c.X - 5, c.Y + 5), new Point(c.X + 5, c.Y - 5));
                    break;
            }
        }
    }
}
