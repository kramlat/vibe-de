using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vde.Shell;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls;

// One of the computer's displays, described for the Display settings page. Width and Height are the
// physical resolution, Scaling the running scale factor; X and Y the pixel position (only set when the
// screens come from the running session, so the Identify overlays can sit on them).
public sealed record DisplayScreen(int Width, int Height, double Scaling, bool IsPrimary, int X = 0, int Y = 0);

// The Display settings page, in the System category: a preview of the displays to select and drag into
// place, Identify, the multiple-displays mode and, per display, brightness, scale, resolution and
// orientation. Every change is saved to HKCU\Software\Vde\Display at once; the part of the session that
// carries screens out applies what it supports.
public sealed class DisplayPageView : UserControl
{
    public const string PageId = "display";
    private const double PreviewWidth = 720, PreviewHeight = 250, PreviewMargin = 20;
    private const int OverlayMs = 2500;
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.Parse("#1f6fc4"));

    private readonly IHive _hive;
    private readonly Func<IReadOnlyList<DisplayScreen>?>? _screenProvider;
    private readonly Canvas _preview = new() { Height = PreviewHeight + 2 * PreviewMargin };
    private readonly ComboBox _modeCombo = new() { MinWidth = 200 };
    private readonly StackPanel _modeChoices = new() { Spacing = 8 };
    private readonly Slider _brightness = new() { Minimum = 0, Maximum = 100, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _nightLight = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _scale = new() { MinWidth = 200 };
    private readonly ComboBox _resolution = new() { MinWidth = 200 };
    private readonly ComboBox _orientation = new() { MinWidth = 200 };
    private readonly List<Border> _tiles = [];
    private readonly List<RadioButton> _modeButtons = [];
    private (Point origin, double baseLeft, double baseTop)? _drag;
    private bool _updating;
    private bool _fromSession;                        // the screens come from the real display server
    private int _fitWidth, _fitHeight;                // the preview's scale denominator, physical units
    private double _fit = 0.12;                       // preview pixels per physical pixel
    private int _selected = 1;                        // displays are numbered from 1, primary first
    private List<DisplayScreen> _screens = [];

    // screenProvider says which displays the session runs; without it (or when it answers nothing) the
    // page shows a single 1920 × 1080 display, so everything is still selectable and storable.
    public DisplayPageView(IHive hive, Func<IReadOnlyList<DisplayScreen>?>? screenProvider = null)
    {
        _hive = hive;
        _screenProvider = screenProvider;

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Select a display to change the settings for it. Drag displays to rearrange them.",
            TextWrapping = TextWrapping.Wrap,
        });

        // The display preview: the numbered rectangles at the relative places the user dragged them to.
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.WhiteSmoke),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _preview,
        });

        var underPreview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var identify = new Button { Content = "Identify", Padding = new Thickness(16, 6) };
        identify.Click += (_, _) => Identify();
        underPreview.Children.Add(identify);
        _modeCombo.SelectionChanged += (_, _) =>
        {
            if (!_updating && _modeCombo.SelectedItem is string label)
            {
                var choice = DisplaySettingsStore.ModeChoices(_screens.Count).FirstOrDefault(c => c.Label == label);
                if (choice is not null) Change(s => s with { MultipleDisplays = choice.Value });
            }
        };
        underPreview.Children.Add(_modeCombo);
        panel.Children.Add(underPreview);

        var multiple = new Expander
        {
            Header = RowHead("Multiple displays", "Choose the presentation mode for your displays", "preferences-desktop-display", "video-display"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _modeChoices,
        };
        panel.Children.Add(multiple);

        panel.Children.Add(new TextBlock { Text = "Brightness", FontSize = 16, FontWeight = FontWeight.SemiBold });
        _brightness.ValueChanged += (_, e) => { if (!_updating) SetBrightness((long)e.NewValue); };
        panel.Children.Add(Card("Brightness", "Change the brightness of the built-in display", ["brightness", "weather-clear-sun", "preferences-desktop-display"], _brightness));
        _nightLight.IsCheckedChanged += (_, _) => { if (!_updating) Change(s => s with { NightLight = _nightLight.IsChecked == true }); };
        panel.Children.Add(Card("Night light", "Warmer colours to help you sleep", ["night-light", "weather-clear-night", "preferences-desktop-display"], _nightLight));

        panel.Children.Add(new TextBlock { Text = "Scale & layout", FontSize = 16, FontWeight = FontWeight.SemiBold });
        _scale.SelectionChanged += (_, _) => { if (!_updating && _scale.SelectedItem is string v) WithDisplay(_selected, c => c with { Scale = ValueFor(_scale, c.Scale, v) }); };
        panel.Children.Add(Card("Scale", "Change the size of text, apps, and other items", ["zoom-fit-best", "preferences-desktop-display"], _scale));
        _resolution.SelectionChanged += (_, _) => { if (!_updating && _resolution.SelectedItem is string v) WithDisplay(_selected, c => c with { Resolution = ValueFor(_resolution, c.Resolution, v) }); };
        panel.Children.Add(Card("Display resolution", "Adjust the resolution to fit your connected display", ["video-display", "preferences-desktop-display"], _resolution));
        _orientation.SelectionChanged += (_, _) => { if (!_updating && _orientation.SelectedItem is string v) WithDisplay(_selected, c => c with { Orientation = ValueFor(_orientation, c.Orientation, v) }); };
        panel.Children.Add(Card("Display orientation", "Turn the display to use it upright or sideways", ["object-rotate-right", "preferences-desktop-display"], _orientation));

        Content = new ScrollViewer { Content = panel };
        Loaded += (_, _) => Reload();
        Reload();
    }

    // For tests and screen readers: the displays as the page runs them (never empty), the preview tiles,
    // and the radio buttons of the multiple-displays expander.
    public IReadOnlyList<DisplayScreen> Screens => _screens;
    public IReadOnlyList<Border> Tiles => _tiles;
    public IReadOnlyList<RadioButton> ModeButtons => _modeButtons;
    public ComboBox ModeCombo => _modeCombo;
    public Slider BrightnessSlider => _brightness;
    public ToggleSwitch NightLightSwitch => _nightLight;
    public ComboBox ScaleCombo => _scale;
    public ComboBox ResolutionCombo => _resolution;
    public ComboBox OrientationCombo => _orientation;
    public int SelectedDisplay => _selected;

    // The saved relative places of the displays, by display number (1 = primary).
    public IReadOnlyDictionary<int, DisplaySlot> LayoutSlots
    {
        get
        {
            var slots = EffectiveSlots();
            var result = new Dictionary<int, DisplaySlot>();
            for (var i = 0; i < _screens.Count; i++)
                result[i + 1] = slots[i];
            return result;
        }
    }

    // The places to draw and to save: what is stored, with the unstored displays lined up side by side
    // after it, everything relative to the top-left display.
    private List<DisplaySlot> EffectiveSlots()
    {
        var stored = DisplaySettingsStore.Load(_hive).Slots;
        var slots = new List<DisplaySlot>();
        for (var i = 0; i < _screens.Count; i++)
            if (i < stored.Count) slots.Add(stored[i]);
            else slots.Add(new DisplaySlot(_screens.Take(i).Sum(s => (double)s.Width), 0));
        return Normalize(slots);
    }

    public void SelectDisplay(int number) { _selected = Math.Clamp(number, 1, _screens.Count); Reload(); }

    public void SetMode(string mode) => Change(s => s with { MultipleDisplays = mode });
    public void SetBrightness(long level) => WithDisplay(_selected, c => c with { Brightness = Math.Clamp(level, 0, 100) });
    public void SetNightLight(bool on) => Change(s => s with { NightLight = on });
    public void SetScale(string percent) => WithDisplay(_selected, c => c with { Scale = percent });
    public void SetResolution(string resolution) => WithDisplay(_selected, c => c with { Resolution = resolution });
    public void SetOrientation(string orientation) => WithDisplay(_selected, c => c with { Orientation = orientation });

    // Dragging a display in the preview, expressed as the tested API: place display `number` at x,y
    // (physical units, relative to the top-left display).
    public void MoveDisplay(int number, double x, double y)
    {
        Change(s =>
        {
            var slots = new List<DisplaySlot>(s.Slots);
            var index = number - 1;
            while (slots.Count <= index) slots.Add(new DisplaySlot(0, 0));
            slots[index] = new DisplaySlot(Math.Max(0, x), Math.Max(0, y));
            return s with { Slots = slots };
        });
    }

    // The stored places are relative to the top-left display, so dragging never moves the whole set.
    private static List<DisplaySlot> Normalize(List<DisplaySlot> slots)
    {
        if (slots.Count == 0) return slots;
        var minX = slots.Min(s => s.X);
        var minY = slots.Min(s => s.Y);
        return slots.Select(s => new DisplaySlot(s.X - minX, s.Y - minY)).ToList();
    }

    // Peek: flash the number over every display briefly, and put a big number window on the real screens
    // when the page runs against them. Returns those overlay windows (empty in tests).
    public List<Window> Identify()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var overlays = new List<Window>();
        if (_fromSession && owner is not null)
            for (var i = 0; i < _screens.Count; i++)
                overlays.Add(NumberOverlay(_screens[i], i + 1, owner));
        foreach (var label in _tiles.Select(t => t.Child).OfType<TextBlock>())
            label.FontSize = 56;
        DispatcherTimer.RunOnce(() => { foreach (var label in _tiles.Select(t => t.Child).OfType<TextBlock>()) label.FontSize = 36; },
            TimeSpan.FromMilliseconds(OverlayMs));
        return overlays;
    }

    private Window NumberOverlay(DisplayScreen screen, int number, Window owner)
    {
        var overlay = new Window
        {
            SystemDecorations = Avalonia.Controls.WindowDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Position = new PixelPoint(screen.X + 60, screen.Y + 60),
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Width = 180, Height = 140, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(230, 31, 111, 196)),
                Child = new TextBlock
                {
                    Text = number.ToString(), FontSize = 72, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        overlay.Show(owner);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OverlayMs) };
        timer.Tick += (_, _) => { timer.Stop(); overlay.Close(); };
        timer.Start();
        return overlay;
    }

    private void WithDisplay(int number, Func<DisplayConfig, DisplayConfig> edit)
    {
        Change(s =>
        {
            var displays = new Dictionary<int, DisplayConfig>(s.Displays)
            {
                [number] = edit(s.Displays.TryGetValue(number, out var c) ? c : new DisplayConfig()),
            };
            return s with { Displays = displays };
        });
    }

    private void Change(Func<DisplaySettings, DisplaySettings> edit)
    {
        DisplaySettingsStore.Save(_hive, edit(DisplaySettingsStore.Load(_hive)));
        Reload();
    }

    private void Reload()
    {
        if (_updating) return;
        _screens = ReadScreens();
        _updating = true;
        try
        {
            var settings = DisplaySettingsStore.Load(_hive);
            BuildModeControls(settings);
            var display = settings.Displays.TryGetValue(_selected, out var c) ? c : new DisplayConfig();
            var screen = _screens[Math.Clamp(_selected, 1, _screens.Count) - 1];
            _brightness.Value = Math.Clamp(display.Brightness, 0, 100);
            _nightLight.IsChecked = settings.NightLight;
            FillCombo(_scale, DisplaySettingsStore.ScaleChoices(screen.Scaling), display.Scale, null);
            FillCombo(_resolution, DisplaySettingsStore.ResolutionChoices(screen.Width, screen.Height), display.Resolution, $"{screen.Width}x{screen.Height}");
            FillCombo(_orientation, DisplaySettingsStore.OrientationChoices, display.Orientation, "Landscape");
        }
        finally { _updating = false; }
        BuildPreview();
    }

    private void BuildModeControls(DisplaySettings settings)
    {
        var choices = DisplaySettingsStore.ModeChoices(_screens.Count);
        _modeCombo.ItemsSource = choices.Select(c => c.Label).ToList();
        _modeCombo.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => c.Value == settings.MultipleDisplays));
        _modeChoices.Children.Clear();
        _modeButtons.Clear();
        foreach (var choice in choices)
        {
            var radio = new RadioButton
            {
                GroupName = "VdeDisplayMode", Content = choice.Label, Margin = new Thickness(12, 0, 0, 0),
                IsChecked = choice.Value == settings.MultipleDisplays,
            };
            radio.IsCheckedChanged += (_, _) => { if (!_updating && radio.IsChecked == true) Change(s => s with { MultipleDisplays = choice.Value }); };
            _modeChoices.Children.Add(radio);
            _modeButtons.Add(radio);
        }
    }

    private void BuildPreview()
    {
        _preview.Children.Clear();
        _tiles.Clear();
        var slotsMap = LayoutSlots;
        var maxRight = _screens.Select((s, i) => (slotsMap.TryGetValue(i + 1, out var p) ? p.X : 0) + s.Width).Max();
        var maxBottom = _screens.Select((s, i) => (slotsMap.TryGetValue(i + 1, out var p) ? p.Y : 0) + s.Height).Max();
        _fitWidth = (int)maxRight;
        _fitHeight = (int)maxBottom;
        if (_fitWidth <= 0 || _fitHeight <= 0) { _fitWidth = 1; _fitHeight = 1; }
        _fit = Math.Min((PreviewWidth - 2 * PreviewMargin) / _fitWidth, (PreviewHeight - 2 * PreviewMargin) / _fitHeight);

        for (var i = 0; i < _screens.Count; i++)
        {
            var number = i + 1;
            var screen = _screens[i];
            var slot = slotsMap.TryGetValue(number, out var p) ? p : new DisplaySlot(0, 0);
            var selected = number == _selected;

            var label = new TextBlock
            {
                Text = number.ToString(), FontSize = 36, FontWeight = FontWeight.SemiBold,
                Foreground = selected ? Brushes.White : ThemeTokens.Find(this, ThemeToken.TextPrimary, Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var tile = new Border
            {
                Width = Math.Max(56, screen.Width * _fit), Height = Math.Max(34, screen.Height * _fit),
                CornerRadius = new CornerRadius(4),
                Background = selected ? SelectedFill : ThemeTokens.Find(this, ThemeToken.Track, Brushes.Gray),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected ? new SolidColorBrush(Color.Parse("#2f82dd")) : ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
                Child = label,
                Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            Canvas.SetLeft(tile, PreviewMargin + slot.X * _fit);
            Canvas.SetTop(tile, PreviewMargin + slot.Y * _fit);
            tile.ZIndex = selected ? 2 : 1;
            _preview.Children.Add(tile);
            _tiles.Add(tile);

            tile.PointerPressed += (_, e) =>
            {
                SelectDisplay(number);
                if (e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                    e.Pointer.Capture(tile);
            };
            tile.PointerMoved += (_, e) =>
            {
                if (e.Pointer.Captured is not Border captured || captured != tile) return;
                var point = e.GetPosition(_preview);
                _drag ??= (point, Canvas.GetLeft(tile), Canvas.GetTop(tile));
                Canvas.SetLeft(tile, Math.Max(PreviewMargin, _drag.Value.baseLeft + (point.X - _drag.Value.origin.X)));
                Canvas.SetTop(tile, Math.Max(PreviewMargin, _drag.Value.baseTop + (point.Y - _drag.Value.origin.Y)));
            };
            tile.PointerReleased += (_, e) =>
            {
                if (e.Pointer.Captured != tile) return;
                e.Pointer.Capture(null);
                if (_drag is { } drag)
                {
                    _drag = null;
                    MoveDisplay(number,
                        Math.Round((Canvas.GetLeft(tile) - PreviewMargin) / _fit),
                        Math.Round((Canvas.GetTop(tile) - PreviewMargin) / _fit));
                }
            };
        }
    }

    // The combo shows labels; the stored value is the plain one ("100", "1920x1080", "Landscape"),
    // which for the running values is the chosen label's value in the same list.
    private string ValueFor(ComboBox combo, string stored, string label)
    {
        var choices = combo == _scale ? DisplaySettingsStore.ScaleChoices(1)
            : combo == _orientation ? DisplaySettingsStore.OrientationChoices
            : DisplaySettingsStore.ResolutionChoices(_fitWidth, _fitHeight);
        return choices.FirstOrDefault(c => c.Label == label)?.Value ?? stored;
    }

    private void FillCombo(ComboBox combo, IReadOnlyList<SettingChoice> choices, string value, string? fallback)
    {
        combo.ItemsSource = choices.Select(c => c.Label).ToList();
        var label = choices.FirstOrDefault(c => c.Value == value)?.Label
            ?? (value is { Length: > 0 } ? value : choices.FirstOrDefault(c => c.Value == fallback)?.Label ?? choices[0].Label);
        combo.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => c.Label == label));
    }

    // A settings row in the card shape: the icon with the name and purpose at the left, the control at
    // the right.
    private Control Card(string title, string description, string[] icons, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4) };
        grid.Children.Add(new ShellIcon { IconNames = icons, IconSize = 24, VerticalAlignment = VerticalAlignment.Center });
        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title },
                new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
        texts.SetValue(Grid.ColumnProperty, 1);
        texts.Margin = new Thickness(12, 0, 12, 0);
        grid.Children.Add(texts);
        control.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(control);
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent),
            BorderBrush = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
    }

    private StackPanel RowHead(string title, string description, params string[] icons) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 12,
        Children =
        {
            new ShellIcon { IconNames = icons, IconSize = 20, VerticalAlignment = VerticalAlignment.Center },
            new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = description, FontSize = 12, Opacity = 0.7 },
                },
            },
        },
    };

    // The session's displays, primary first; a session that does not answer shows one display.
    private List<DisplayScreen> ReadScreens()
    {
        var raw = _screenProvider?.Invoke();
        var fromSession = raw is null;   // the live session answers through TopLevel, not a provider
        if (raw is null)
            raw = FromTopLevel();
        _fromSession = fromSession && raw?.Count > 0;
        var ordered = raw?.OrderByDescending(s => s.IsPrimary).ThenBy(s => s.X).ThenBy(s => s.Y).ToList();
        if (ordered is not { Count: > 0 })
            ordered = [new DisplayScreen(1920, 1080, 1, true)];
        return ordered;

        List<DisplayScreen>? FromTopLevel()
        {
            if (TopLevel.GetTopLevel(this)?.Screens is not { ScreenCount: > 0 } screens) return null;
            return screens.All
                .Select(s => new DisplayScreen((int)s.Bounds.Width, (int)s.Bounds.Height, s.Scaling, s.IsPrimary, s.Bounds.X, s.Bounds.Y))
                .ToList();
        }
    }
}
