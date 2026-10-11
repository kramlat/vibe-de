using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Vde.Shell.Controls;

// Draws the first available icon from a list of freedesktop names (or an absolute image path).
public sealed class ShellIcon : Control
{
    public static readonly StyledProperty<IReadOnlyList<string>?> IconNamesProperty =
        AvaloniaProperty.Register<ShellIcon, IReadOnlyList<string>?>(nameof(IconNames));

    public static readonly StyledProperty<int> IconSizeProperty =
        AvaloniaProperty.Register<ShellIcon, int>(nameof(IconSize), 16);

    private IImage? _image;
    private ShellIconProvider? _subscribedTo;

    static ShellIcon()
    {
        AffectsMeasure<ShellIcon>(IconSizeProperty);
        AffectsRender<ShellIcon>(IconNamesProperty, IconSizeProperty);
    }

    public IReadOnlyList<string>? IconNames
    {
        get => GetValue(IconNamesProperty);
        set => SetValue(IconNamesProperty, value);
    }

    // Logical pixels; the control is square.
    public int IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    // Shorthand for a single name.
    public string? IconName
    {
        get => IconNames?.FirstOrDefault();
        set => IconNames = value is null ? null : [value];
    }

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconNamesProperty || change.Property == IconSizeProperty)
            Reload();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscribedTo = ShellIconProvider.Default;
        _subscribedTo.ThemeChanged += OnThemeChanged;
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribedTo is not null)
            _subscribedTo.ThemeChanged -= OnThemeChanged;
        _subscribedTo = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        var names = IconNames;
        double scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1;
        _image = names is { Count: > 0 } ? ShellIconProvider.Default.Load(names, IconSize, scale) : null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_image is not { } image || image.Size.Width <= 0 || image.Size.Height <= 0)
            return;

        // Fit inside the square, keeping the aspect ratio.
        double factor = Math.Min(Bounds.Width / image.Size.Width, Bounds.Height / image.Size.Height);
        var size = new Size(image.Size.Width * factor, image.Size.Height * factor);
        var target = new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        context.DrawImage(image, new Rect(image.Size), target);
    }
}
