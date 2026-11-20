using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Vibe.Registry;

namespace Vde.Shell.Theme;

// Installs a theme into the running Avalonia application: the base Avalonia theme (Fluent, Simple or none),
// the shell's colour tokens for both light and dark, and the light/dark variant itself.
// Call from the UI thread.
public sealed class ThemeEngine
{
    // Where each token comes from in the base themes (Avalonia 11.3 resource names).
    private static readonly Dictionary<ThemeToken, string> FluentKeys = new()
    {
        [ThemeToken.Accent] = "SystemControlHighlightAccentBrush",
        [ThemeToken.TextPrimary] = "SystemControlForegroundBaseHighBrush",
        [ThemeToken.TextSecondary] = "SystemControlForegroundBaseMediumBrush",
        [ThemeToken.Surface] = "SystemControlBackgroundAltHighBrush",
        [ThemeToken.Border] = "SystemControlForegroundBaseLowBrush",
        [ThemeToken.HoverFill] = "SystemControlHighlightListLowBrush",
        [ThemeToken.SelectedFill] = "SystemControlHighlightListMediumBrush",
        [ThemeToken.Track] = "SystemControlBackgroundBaseLowBrush",
    };

    private static readonly Dictionary<ThemeToken, string> SimpleKeys = new()
    {
        [ThemeToken.Accent] = "HighlightBrush",
        [ThemeToken.TextPrimary] = "ThemeForegroundBrush",
        [ThemeToken.TextSecondary] = "ThemeForegroundLowBrush",
        [ThemeToken.Surface] = "ThemeBackgroundBrush",
        [ThemeToken.Border] = "ThemeBorderMidBrush",
        [ThemeToken.HoverFill] = "ThemeControlMidBrush",
        [ThemeToken.SelectedFill] = "ThemeControlMidHighBrush",
        [ThemeToken.Track] = "ThemeBorderLowBrush",
        [ThemeToken.Critical] = "ErrorBrush",
    };

    // Used when there is no base theme, or it lacks a key.
    private static Color Default(ThemeToken token, bool dark) => token switch
    {
        ThemeToken.Accent => Color.Parse("#0078D7"),
        ThemeToken.TextPrimary => dark ? Colors.White : Colors.Black,
        ThemeToken.TextSecondary => dark ? Color.Parse("#99FFFFFF") : Color.Parse("#99000000"),
        ThemeToken.Surface => dark ? Color.Parse("#202020") : Colors.White,
        ThemeToken.Border => dark ? Color.Parse("#33FFFFFF") : Color.Parse("#33000000"),
        ThemeToken.HoverFill => dark ? Color.Parse("#19FFFFFF") : Color.Parse("#19000000"),
        ThemeToken.SelectedFill => dark ? Color.Parse("#33FFFFFF") : Color.Parse("#33000000"),
        ThemeToken.Track => dark ? Color.Parse("#33FFFFFF") : Color.Parse("#33000000"),
        ThemeToken.Critical => dark ? Color.Parse("#FF99A4") : Color.Parse("#C42B1C"),
        _ => Colors.Gray,
    };

    private IStyle? _baseStyle;
    private ResourceDictionary? _tokens;
    private bool _bindingPending;
    private bool _applied;

    public ThemeEngine(ThemeCatalog catalog) => Catalog = catalog;

    public ThemeCatalog Catalog { get; }
    public ThemeSelection Selection { get; private set; } = new();
    public ThemeInfo Theme { get; private set; } = ThemeCatalog.Fluent;

    // Raised after a theme has been installed.
    public event EventHandler? Changed;

    // Raised when the Avalonia base theme itself was replaced (Fluent <-> Simple). Colours, accent and the
    // light/dark variant update in open windows immediately, but Avalonia does not re-style the stock chrome of
    // windows that were already open across a base-theme swap: windows opened afterwards are correct. A host
    // with long-lived windows (the taskbar) can rebuild them when this fires.
    public event EventHandler? BaseThemeChanged;

    public void Apply(ThemeSelection selection)
    {
        var app = Application.Current ?? throw new InvalidOperationException("There is no running Avalonia application.");
        var theme = Catalog.FindOrDefault(selection.ThemeId);
        var accent = selection.Accent ?? theme.Accent;
        bool baseChanged = _applied && Theme.Base != theme.Base;

        if (_baseStyle is not null)
            app.Styles.Remove(_baseStyle);
        _baseStyle = CreateBase(theme.Base, accent);
        if (_baseStyle is not null)
            app.Styles.Insert(0, _baseStyle);   // first, so anything the app adds still wins

        if (_tokens is not null)
            app.Resources.MergedDictionaries.Remove(_tokens);
        _tokens = BuildTokens(theme, _baseStyle, accent);
        app.Resources.MergedDictionaries.Add(_tokens);

        app.RequestedThemeVariant = theme.Variant != VariantPreference.System ? ToVariant(theme.Variant) : ToVariant(selection.Variant);

        Selection = selection;
        Theme = theme;
        _applied = true;
        Changed?.Invoke(this, EventArgs.Empty);
        ThemeTokens.RaiseChanged();
        if (baseChanged)
            BaseThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    // Takes the theme back out of the application.
    public void Uninstall()
    {
        var app = Application.Current;
        if (app is null)
            return;
        if (_baseStyle is not null)
            app.Styles.Remove(_baseStyle);
        if (_tokens is not null)
            app.Resources.MergedDictionaries.Remove(_tokens);
        _baseStyle = null;
        _tokens = null;
        ThemeTokens.RaiseChanged();
    }

    // Applies the registry's choice now and again whenever it changes, however it was changed (Settings,
    // a script, another program). Dispose the result to stop listening.
    public IDisposable BindTo(IHive hive)
    {
        Apply(ThemeSettings.Load(hive));

        void OnChanged(object? sender, RegistryChange change)
        {
            if (!change.IsUnder(ThemeSettings.KeyPath) || _bindingPending)
                return;
            _bindingPending = true;   // several values saved in a row become one re-theme
            Dispatcher.UIThread.Post(() =>
            {
                _bindingPending = false;
                Apply(ThemeSettings.Load(hive));
            });
        }

        hive.Changed += OnChanged;
        return new Unsubscribe(() => hive.Changed -= OnChanged);
    }

    private static ThemeVariant ToVariant(VariantPreference preference) => preference switch
    {
        VariantPreference.Light => ThemeVariant.Light,
        VariantPreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private static IStyle? CreateBase(ThemeBase themeBase, Color? accent)
    {
        switch (themeBase)
        {
            case ThemeBase.Fluent:
                var fluent = new FluentTheme();
                if (accent is { } color)
                {
                    // Restyles every stock control, not just the shell's own.
                    fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = color };
                    fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = color };
                }
                return fluent;
            case ThemeBase.Simple:
                return new SimpleTheme();   // has no accent palette; the shell's own tokens still follow the accent
            default:
                return null;
        }
    }

    private static ResourceDictionary BuildTokens(ThemeInfo theme, IStyle? baseStyle, Color? accent)
    {
        var root = new ResourceDictionary();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            bool dark = variant == ThemeVariant.Dark;
            var overrides = dark ? theme.DarkTokens : theme.LightTokens;
            var inner = new ResourceDictionary();

            foreach (var token in Enum.GetValues<ThemeToken>())
            {
                Color color;
                if (overrides.TryGetValue(token, out var own))
                    color = own;
                else if (token == ThemeToken.Accent && accent is { } chosen)
                    color = chosen;
                else if (!TryFromBase(baseStyle, theme.Base, token, variant, out color))
                    color = Default(token, dark);
                inner[ThemeTokens.Key(token)] = new SolidColorBrush(color);
            }

            // The theme's colours for the base theme's own resources (Simple's Theme* brushes).
            foreach (var (name, color) in dark ? theme.DarkPalette : theme.LightPalette)
            {
                inner[name + "Color"] = color;
                inner[name + "Brush"] = new SolidColorBrush(color);
            }
            if (accent is { } pick && theme.Base == ThemeBase.Simple)   // the user's accent beats the theme's own
            {
                inner["HighlightColor"] = pick;
                inner["HighlightBrush"] = new SolidColorBrush(pick);
                inner["ThemeAccentColor"] = pick;
                inner["ThemeAccentBrush"] = new SolidColorBrush(pick);
            }

            // A Windows 11 dialog button: a solid panel on the window's Mica, a hairline border, a slightly
            // darker hover. The Fluent theme reads these exact resource keys; leaving them unset leaves the
            // translucent stock fill, which goes muddy over the washed wallpaper.
            inner["ButtonBackground"] = new SolidColorBrush(dark ? Color.Parse("#2E2E2E") : Colors.White);
            inner["ButtonBackgroundPointerOver"] = new SolidColorBrush(dark ? Color.Parse("#383838") : Color.Parse("#F6F6F6"));
            inner["ButtonBackgroundPressed"] = new SolidColorBrush(dark ? Color.Parse("#424242") : Color.Parse("#F0F0F0"));
            inner["ButtonBorderBrush"] = new SolidColorBrush(dark ? Color.Parse("#4A4A4A") : Color.Parse("#D9D9D9"));
            inner["ButtonBorderBrushPointerOver"] = new SolidColorBrush(dark ? Color.Parse("#5C5C5C") : Color.Parse("#CFCFCF"));
            inner["ButtonBorderBrushPressed"] = new SolidColorBrush(dark ? Color.Parse("#666666") : Color.Parse("#C9C9C9"));
            root.ThemeDictionaries[variant] = inner;
        }
        root["Vde.Classic"] = theme.Classic;
        root["Vde.ClassicBarFrom"] = theme.ClassicBarFrom ?? Color.Parse("#0A246A");
        root["Vde.ClassicBarTo"] = theme.ClassicBarTo ?? Color.Parse("#A6CAF0");
        root["ControlCornerRadius"] = new CornerRadius(theme.CornerRadius ?? 4);   // Windows 11 rounds its controls at 4
        return root;
    }

    private static bool TryFromBase(IStyle? baseStyle, ThemeBase themeBase, ThemeToken token, ThemeVariant variant, out Color color)
    {
        color = default;
        var keys = themeBase == ThemeBase.Simple ? SimpleKeys : FluentKeys;
        if (baseStyle is not IResourceNode node || !keys.TryGetValue(token, out var key) || !node.TryGetResource(key, variant, out var value))
            return false;

        switch (value)
        {
            case ISolidColorBrush brush:
                color = brush.Color;
                return true;
            case Color c:
                color = c;
                return true;
            default:
                return false;
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
