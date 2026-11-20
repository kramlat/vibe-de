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
    private IBrush? _mica;
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
        // Mica when the shell set a wallpaper backdrop (WallpaperMica), otherwise the theme's surface.
        var classic = ThemeTokens.IsClassic(this);
        Background = classic ? ClassicBar(_active) : _mica ?? ThemeTokens.Find(this, ThemeToken.Surface, new SolidColorBrush(Color.Parse("#202030")));
        CornerRadius = classic ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);
        var primary = ThemeTokens.Find(this, ThemeToken.TextPrimary, Brushes.White);
        var secondary = ThemeTokens.Find(this, ThemeToken.TextSecondary, Brushes.Gray);
        _title.Foreground = classic ? Brushes.White : _active ? primary : secondary;
        _title.FontWeight = classic ? FontWeight.Bold : FontWeight.Normal;
        _icon.Opacity = _active ? 1 : 0.6;
        foreach (var g in _glyphs) g.InvalidateVisual();
    }

    private Color ClassicColor(string key, string fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color c ? c : Color.Parse(fallback);

    // The classic titlebar: navy fading to light blue when active, grey fading to light grey when not.
    private IBrush ClassicBar(bool active) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = active
            ? [new GradientStop(ClassicColor("Vde.ClassicBarFrom", "#0A246A"), 0), new GradientStop(ClassicColor("Vde.ClassicBarTo", "#A6CAF0"), 1)]
            : [new GradientStop(Color.Parse("#808080"), 0), new GradientStop(Color.Parse("#C0C0C0"), 1)],
    };

    // The titlebar's Mica: a blurred, tinted sample of the wallpaper, or null for the theme's plain surface.
    // Set once by the renderer that produces the titlebar images; it survives Apply (and theme changes).
    public void SetBackdrop(IBrush? mica)
    {
        _mica = mica;
        Apply();
    }

    // One button, drawn by the same code as every other titlebar button (CaptionButton.Draw); vwm tells us where the pointer is.
    private sealed class Glyph(WindowChromeView owner, ChromeButton kind) : Control
    {
        public override void Render(DrawingContext context)
        {
            var hovered = owner._hover == kind;
            var caption = kind switch
            {
                ChromeButton.Minimize => CaptionKind.Minimize,
                ChromeButton.Maximize => owner._maximized ? CaptionKind.Restore : CaptionKind.Maximize,
                _ => CaptionKind.Close,
            };
            CaptionButton.Draw(context, new Rect(Bounds.Size), caption, hovered, hovered && owner._pressed == kind, owner._active, owner);
        }
    }
}
