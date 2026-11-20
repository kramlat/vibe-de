using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Vde.Freedesktop;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Theme.Tests;

public sealed class ThemeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vde-theme-" + Guid.NewGuid().ToString("N"));
    public XdgDirectories Xdg { get; }

    public ThemeFixture()
    {
        Directory.CreateDirectory(Root);
        var env = new Dictionary<string, string>
        {
            ["HOME"] = Root,
            ["XDG_DATA_HOME"] = Path.Combine(Root, "user"),
            ["XDG_DATA_DIRS"] = Path.Combine(Root, "system"),
        };
        Xdg = new XdgDirectories(k => env.GetValueOrDefault(k));
    }

    public void Theme(string where, string id, string content)
    {
        var dir = Path.Combine(Root, where, "vde", "themes", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.theme"), content);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class CatalogTests
{
    [Fact]
    public void Built_in_themes_are_always_there()
    {
        using var fx = new ThemeFixture();
        var catalog = ThemeCatalog.Discover(fx.Xdg);

        Assert.Equal(new[] { "fluent", "simple", "breeze", "redmond", "redmond98" }, catalog.Themes.Select(t => t.Id));
        Assert.All(catalog.Themes, t => Assert.True(t.IsBuiltIn));
        Assert.Equal("fluent", catalog.FindOrDefault("no-such-theme").Id);
    }

    [Fact]
    public void Reads_user_themes_with_tokens_and_variant_overrides()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "Midnight", """
            [VDE Theme]
            Name=Midnight
            Comment=Dark and purple
            Author=Someone
            Base=Simple
            Variant=Dark
            Accent=#ff6a00

            [Tokens]
            TextSecondary=#99ffffff
            Surface=#101018

            [Tokens Dark]
            Surface=#000000
            Critical=#ff0000
            """);

        var catalog = ThemeCatalog.Discover(fx.Xdg);
        var theme = catalog.Find("MIDNIGHT")!;   // ids are case-insensitive

        Assert.Equal("Midnight", theme.Name);
        Assert.Equal("Dark and purple", theme.Comment);
        Assert.Equal(ThemeBase.Simple, theme.Base);
        Assert.Equal(VariantPreference.Dark, theme.Variant);
        Assert.Equal(Color.Parse("#ff6a00"), theme.Accent);
        Assert.False(theme.IsBuiltIn);
        Assert.Equal(Color.Parse("#101018"), theme.LightTokens[ThemeToken.Surface]);       // shared group
        Assert.Equal(Color.Parse("#000000"), theme.DarkTokens[ThemeToken.Surface]);        // variant group wins
        Assert.Equal(Color.Parse("#99ffffff"), theme.DarkTokens[ThemeToken.TextSecondary]);
        Assert.False(theme.LightTokens.ContainsKey(ThemeToken.Critical));
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public void User_themes_shadow_system_ones_and_cannot_replace_built_ins()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "nord", "[VDE Theme]\nName=Nord (mine)\n");
        fx.Theme("system", "nord", "[VDE Theme]\nName=Nord (system)\n");
        fx.Theme("system", "fluent", "[VDE Theme]\nName=Fake Fluent\n");

        var catalog = ThemeCatalog.Discover(fx.Xdg);

        Assert.Equal("Nord (mine)", catalog.Find("nord")!.Name);
        Assert.Equal("Fluent", catalog.Find("fluent")!.Name);
        Assert.Equal(6, catalog.Themes.Count);
    }

    [Fact]
    public void Bad_values_are_reported_and_ignored_not_fatal()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "odd", "[VDE Theme]\nBase=Metal\nVariant=Sepia\nAccent=notacolour\n[Tokens]\nTextSecondary=zzz\nTrack=#112233\n");
        fx.Theme("user", "nogroup", "Name=x\n");

        var catalog = ThemeCatalog.Discover(fx.Xdg);
        var odd = catalog.Find("odd")!;

        Assert.Equal(ThemeBase.Fluent, odd.Base);
        Assert.Equal(VariantPreference.System, odd.Variant);
        Assert.Null(odd.Accent);
        Assert.False(odd.LightTokens.ContainsKey(ThemeToken.TextSecondary));
        Assert.Equal(Color.Parse("#112233"), odd.LightTokens[ThemeToken.Track]);
        Assert.Null(catalog.Find("nogroup"));
        Assert.Equal(5, catalog.Errors.Count);   // Base, Variant, Accent, the token, and the missing group
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_when_the_registry_has_nothing()
        => Assert.Equal(new ThemeSelection(), ThemeSettings.Load(new Hive("HKCU")));

    [Fact]
    public void Round_trips_through_the_registry_and_through_a_file()
    {
        var hive = new Hive("HKCU");
        ThemeSettings.Save(hive, new ThemeSelection("midnight", VariantPreference.Dark, Color.Parse("#ff6a00")));

        var ms = new MemoryStream();
        hive.Write(ms);
        ms.Position = 0;
        var loaded = ThemeSettings.Load(Hive.Read("HKCU", ms));

        Assert.Equal("midnight", loaded.ThemeId);
        Assert.Equal(VariantPreference.Dark, loaded.Variant);
        Assert.Equal(Color.Parse("#ff6a00"), loaded.Accent);

        ThemeSettings.Save(hive, loaded with { Accent = null });
        Assert.Null(ThemeSettings.Load(hive).Accent);
    }

    [Fact]
    public void Garbage_values_fall_back_to_defaults()
    {
        var hive = new Hive("HKCU");
        hive.SetValue(ThemeSettings.KeyPath, "Theme", RegistryValueKind.DWord, 5u);
        hive.SetValue(ThemeSettings.KeyPath, "Variant", RegistryValueKind.String, "Sepia");
        hive.SetValue(ThemeSettings.KeyPath, "Accent", RegistryValueKind.String, "purple-ish");

        Assert.Equal(new ThemeSelection(), ThemeSettings.Load(hive));
    }
}

public class EngineTests
{
    private static Color Token(Control host, ThemeToken token, ThemeVariant variant)
    {
        Assert.True(host.TryFindResource(ThemeTokens.Key(token), variant, out var value), $"{token} missing in {variant}");
        return ((ISolidColorBrush)value!).Color;
    }

    private static double Brightness(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000.0;

    private static ThemeEngine Engine(ThemeFixture fx) => new(ThemeCatalog.Discover(fx.Xdg));

    [AvaloniaFact]
    public void Fluent_publishes_every_token_for_both_variants_with_sensible_contrast()
    {
        using var fx = new ThemeFixture();
        var engine = Engine(fx);
        try
        {
            engine.Apply(new ThemeSelection("fluent"));
            var host = new Window();

            foreach (var token in Enum.GetValues<ThemeToken>())
            {
                Token(host, token, ThemeVariant.Light);
                Token(host, token, ThemeVariant.Dark);
            }
            Assert.True(Brightness(Token(host, ThemeToken.TextPrimary, ThemeVariant.Light)) < 60);
            Assert.True(Brightness(Token(host, ThemeToken.TextPrimary, ThemeVariant.Dark)) > 195);
            Assert.True(Brightness(Token(host, ThemeToken.Surface, ThemeVariant.Light)) > 195);
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void Switching_between_fluent_and_simple_swaps_the_base_theme_and_tokens()
    {
        using var fx = new ThemeFixture();
        var engine = Engine(fx);
        var app = Application.Current!;
        var before = app.Styles.Count;
        try
        {
            engine.Apply(new ThemeSelection("fluent"));
            Assert.IsType<FluentTheme>(app.Styles[0]);
            var host = new Window();
            var fluentAccent = Token(host, ThemeToken.Accent, ThemeVariant.Light);

            engine.Apply(new ThemeSelection("simple"));

            Assert.IsType<SimpleTheme>(app.Styles[0]);
            Assert.Equal(before + 1, app.Styles.Count);   // replaced, not stacked
            Assert.NotEqual(fluentAccent, Token(host, ThemeToken.Accent, ThemeVariant.Light));
            foreach (var token in Enum.GetValues<ThemeToken>())
                Token(host, token, ThemeVariant.Dark);
        }
        finally { engine.Uninstall(); }
        Assert.Equal(before, app.Styles.Count);
    }

    [AvaloniaFact]
    public void A_custom_theme_overrides_some_tokens_and_inherits_the_rest_from_its_base()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "mine", "[VDE Theme]\nBase=Fluent\n[Tokens]\nCritical=#123456\n[Tokens Dark]\nTrack=#abcdef\n");
        var engine = Engine(fx);
        try
        {
            engine.Apply(new ThemeSelection("mine"));
            var host = new Window();

            Assert.Equal(Color.Parse("#123456"), Token(host, ThemeToken.Critical, ThemeVariant.Light));
            Assert.Equal(Color.Parse("#123456"), Token(host, ThemeToken.Critical, ThemeVariant.Dark));
            Assert.Equal(Color.Parse("#abcdef"), Token(host, ThemeToken.Track, ThemeVariant.Dark));
            Assert.NotEqual(Color.Parse("#abcdef"), Token(host, ThemeToken.Track, ThemeVariant.Light));
            Assert.True(Brightness(Token(host, ThemeToken.TextPrimary, ThemeVariant.Dark)) > 195);   // from Fluent
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void An_accent_choice_reaches_the_tokens_and_the_stock_controls()
    {
        using var fx = new ThemeFixture();
        var engine = Engine(fx);
        var accent = Color.Parse("#ff6a00");
        try
        {
            engine.Apply(new ThemeSelection("fluent", VariantPreference.System, accent));
            var host = new Window();

            Assert.Equal(accent, Token(host, ThemeToken.Accent, ThemeVariant.Light));
            Assert.Equal(accent, Token(host, ThemeToken.Accent, ThemeVariant.Dark));
            Assert.True(host.TryFindResource("SystemAccentColor", ThemeVariant.Light, out var stock));
            Assert.Equal(accent, (Color)stock!);
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void The_variant_follows_the_user_unless_the_theme_pins_one()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "night", "[VDE Theme]\nVariant=Dark\n");
        var engine = Engine(fx);
        var app = Application.Current!;
        try
        {
            engine.Apply(new ThemeSelection("fluent", VariantPreference.Dark));
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            engine.Apply(new ThemeSelection("fluent", VariantPreference.Light));
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            engine.Apply(new ThemeSelection("fluent", VariantPreference.System));
            Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);

            engine.Apply(new ThemeSelection("night", VariantPreference.Light));   // the theme only works dark
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void A_theme_with_no_base_gets_built_in_token_defaults_and_no_avalonia_theme()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "bare", "[VDE Theme]\nBase=None\n[Tokens Light]\nAccent=#00ff00\n");
        var engine = Engine(fx);
        var app = Application.Current!;
        var before = app.Styles.Count;
        try
        {
            engine.Apply(new ThemeSelection("bare"));
            var host = new Window();

            Assert.Equal(before, app.Styles.Count);
            Assert.Equal(Color.Parse("#00ff00"), Token(host, ThemeToken.Accent, ThemeVariant.Light));
            Assert.Equal(Color.Parse("#0078D7"), Token(host, ThemeToken.Accent, ThemeVariant.Dark));   // default
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void An_unknown_theme_id_falls_back_to_fluent_and_changed_is_raised()
    {
        using var fx = new ThemeFixture();
        var engine = Engine(fx);
        int raised = 0;
        engine.Changed += (_, _) => raised++;
        try
        {
            engine.Apply(new ThemeSelection("deleted-theme"));

            Assert.Equal("fluent", engine.Theme.Id);
            Assert.Equal("deleted-theme", engine.Selection.ThemeId);   // the choice is remembered even if it cannot be shown
            Assert.Equal(1, raised);
        }
        finally { engine.Uninstall(); }
    }

    [AvaloniaFact]
    public void Binding_to_the_registry_retheme_live_and_coalesces_a_burst_of_writes()
    {
        using var fx = new ThemeFixture();
        fx.Theme("user", "night", "[VDE Theme]\nBase=Simple\nVariant=Dark\n");
        var engine = Engine(fx);
        int applied = 0;
        engine.Changed += (_, _) => applied++;
        var hive = new Hive("HKCU");
        try
        {
            using (engine.BindTo(hive))
            {
                Assert.Equal("fluent", engine.Theme.Id);   // initial apply: nothing in the registry yet
                Assert.Equal(1, applied);

                ThemeSettings.Save(hive, new ThemeSelection("night", VariantPreference.Light, Color.Parse("#ff6a00")));   // 3 writes
                Assert.Equal(1, applied);                  // nothing happens until the UI thread gets to it
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(2, applied);                  // ...and then exactly once
                Assert.Equal("night", engine.Theme.Id);
                Assert.Equal(Color.Parse("#ff6a00"), engine.Selection.Accent);
                Assert.IsType<SimpleTheme>(Application.Current!.Styles[0]);
            }

            ThemeSettings.Save(hive, new ThemeSelection("fluent"));   // unbound: ignored
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, applied);
            Assert.Equal("night", engine.Theme.Id);
        }
        finally { engine.Uninstall(); }
    }
}
