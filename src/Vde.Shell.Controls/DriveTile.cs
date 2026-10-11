using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// A "This PC" style tile: icon, name, an optional usage bar and a detail line.
public sealed class DriveTile : Border
{
    public static readonly StyledProperty<IReadOnlyList<string>?> IconNamesProperty =
        AvaloniaProperty.Register<DriveTile, IReadOnlyList<string>?>(nameof(IconNames));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DriveTile, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<DriveTile, string?>(nameof(Detail));

    // 0..1 fullness; NaN hides the bar (network shares often cannot say).
    public static readonly StyledProperty<double> UsageProperty =
        AvaloniaProperty.Register<DriveTile, double>(nameof(Usage), double.NaN);

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<DriveTile, bool>(nameof(IsSelected));

    private readonly ShellIcon _icon = new() { IconSize = 48, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly UsageBar _bar = new() { Margin = new Thickness(0, 4) };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _hover;

    static DriveTile()
    {
        IconNamesProperty.Changed.AddClassHandler<DriveTile>((t, e) => t._icon.IconNames = (IReadOnlyList<string>?)e.NewValue);
        TitleProperty.Changed.AddClassHandler<DriveTile>((t, e) => t._title.Text = (string?)e.NewValue);
        DetailProperty.Changed.AddClassHandler<DriveTile>((t, e) => t._detail.Text = (string?)e.NewValue);
        UsageProperty.Changed.AddClassHandler<DriveTile>((t, e) => t.ShowUsage((double)e.NewValue!));
        IsSelectedProperty.Changed.AddClassHandler<DriveTile>((t, e) =>
        {
            t.PseudoClasses.Set(":selected", (bool)e.NewValue!);
            t.UpdateBrushes();
        });
    }

    public DriveTile()
    {
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(6);
        MinWidth = 260;
        Background = Brushes.Transparent;   // hit-testable, so hover works over the whole tile
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        Focusable = true;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _bar, _detail } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        Child = grid;

        ShowUsage(double.NaN);

        PointerEntered += (_, _) => { _hover = true; UpdateBrushes(); };
        PointerExited += (_, _) => { _hover = false; UpdateBrushes(); };
        ActualThemeVariantChanged += (_, _) => UpdateBrushes();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeTokens.Changed += OnThemeChanged;
        UpdateBrushes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeTokens.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateBrushes();

    // Theme brushes are looked up here, against the current theme variant, instead of through style
    // setters: setters inside a control's own Styles did not follow a runtime light/dark switch.
    private void UpdateBrushes()
    {
        _detail.Foreground = Token(ThemeToken.TextSecondary, Brushes.Gray);
        Background = IsSelected ? Token(ThemeToken.SelectedFill, Brushes.Transparent)
            : _hover ? Token(ThemeToken.HoverFill, Brushes.Transparent)
            : Brushes.Transparent;
        BorderBrush = IsSelected ? Token(ThemeToken.Accent, Brushes.RoyalBlue) : Brushes.Transparent;
    }

    private IBrush Token(ThemeToken token, IBrush fallback) => ThemeTokens.Find(this, token, fallback);

    public IReadOnlyList<string>? IconNames
    {
        get => GetValue(IconNamesProperty);
        set => SetValue(IconNamesProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static DriveTile For(Volume volume)
    {
        var usage = volume.TotalBytes > 0 ? 1.0 - (double)volume.FreeBytes / volume.TotalBytes : double.NaN;
        return new DriveTile
        {
            IconNames = volume.Names(),
            Title = volume.Label,
            Usage = usage,
            Detail = volume.TotalBytes > 0
                ? $"{ByteFormat.Format(volume.FreeBytes)} free of {ByteFormat.Format(volume.TotalBytes)}"
                : volume.MountPoint,
        };
    }

    public static DriveTile For(NetworkShare share) => new()
    {
        IconNames = share.Names(),
        Title = share.Name,
        Detail = share.Url,
    };

    private void ShowUsage(double usage)
    {
        _bar.IsVisible = !double.IsNaN(usage);
        _bar.Value = usage;
    }
}
