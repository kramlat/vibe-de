using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace Vde.Shell.Controls.Tests;

public class PersonalizationTests
{
    [Fact]
    public void Appearance_settings_round_trip_and_the_recent_list_is_newest_first_without_duplicates()
    {
        var hive = new Hive("HKCU");
        Assert.True(ThemeSettings.Load(hive).Transparency);   // defaults: transparency on, accent not on the bars
        Assert.False(ThemeSettings.Load(hive).AccentOnTaskbar);

        var recent = (IReadOnlyList<Color>?)null;
        foreach (var hex in new[] { "#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#222222" })
            recent = ThemeSettings.WithRecent(recent, Color.Parse(hex));
        ThemeSettings.Save(hive, new ThemeSelection("breeze", VariantPreference.Dark, Color.Parse("#3DAEE9"), false, true, true, recent));

        var loaded = ThemeSettings.Load(hive);
        Assert.Equal("breeze", loaded.ThemeId);
        Assert.False(loaded.Transparency);
        Assert.True(loaded.AccentOnTaskbar && loaded.AccentOnTitleBars);
        Assert.Equal(new[] { "#222222", "#666666", "#555555", "#444444", "#333333" }, loaded.RecentAccents!.Select(c => "#" + c.ToString()[3..]));
    }

    [AvaloniaFact]
    public void Colors_page_saves_mode_accent_toggles_and_remembers_recent_colors()
    {
        var hive = new Hive("HKCU");
        var page = new ColorsPageView(hive);
        new Window { Content = page, Width = 900, Height = 900 }.Show();

        Assert.Equal(48, page.PaletteSwatches.Count);
        Assert.False(page.AccentChoicesVisible);   // Automatic: the theme's own accent

        page.SetMode(VariantPreference.Dark);
        page.TransparencyToggle.IsChecked = false;
        page.TaskbarToggle.IsChecked = true;
        page.PaletteSwatches[10].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // a palette colour

        var s = ThemeSettings.Load(hive);
        Assert.Equal(VariantPreference.Dark, s.Variant);
        Assert.False(s.Transparency);
        Assert.True(s.AccentOnTaskbar);
        Assert.Equal(Color.Parse(ColorsPageView.Palette[10]), s.Accent);
        Assert.Equal(1, page.AccentModeBox.SelectedIndex);   // Manual, with the choices showing
        Assert.True(page.AccentChoicesVisible);
        Assert.Single(page.RecentSwatches);

        Assert.False(page.SetCustom("blue"));
        Assert.NotNull(page.CustomError);
        Assert.True(page.SetCustom("#12AB34"));
        Assert.Equal(Color.Parse("#12AB34"), ThemeSettings.Load(hive).Accent);
        Assert.Equal(2, page.RecentSwatches.Count);

        page.AccentModeBox.SelectedIndex = 0;   // back to Automatic
        Assert.Null(ThemeSettings.Load(hive).Accent);
    }

    [AvaloniaFact]
    public void Style_gallery_lists_every_style_and_choosing_one_saves_it()
    {
        var hive = new Hive("HKCU");
        var page = new ThemesPageView(hive, ThemeCatalog.Discover(TestTheme.EmptyXdg()));
        new Window { Content = page, Width = 900, Height = 900 }.Show();

        Assert.Equal(new[] { "fluent", "simple", "breeze", "redmond" }, page.Tiles.Select(t => (string)t.Tag!));
        Assert.Equal(0, page.SelectedTile);

        page.Tiles[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("redmond", ThemeSettings.Load(hive).ThemeId);
        Assert.Equal(VariantPreference.Light, ThemeSettings.Load(hive).Variant);   // Redmond only exists in light
        Assert.Equal("Redmond", page.CurrentThemeName);
        Assert.Equal(3, page.SelectedTile);
    }

    [AvaloniaFact]
    public void Breeze_and_Redmond_install_with_their_colors_and_corners_and_the_users_accent_wins()
    {
        var engine = TestTheme.Engine;
        engine.Apply(new ThemeSelection("redmond", VariantPreference.Light));
        var app = Avalonia.Application.Current!;
        Assert.True(app.TryFindResource("ThemeBackgroundBrush", app.ActualThemeVariant, out var face));
        Assert.Equal(Color.Parse("#D4D0C8"), ((ISolidColorBrush)face!).Color);
        Assert.True(app.TryFindResource("ControlCornerRadius", out var radius));
        Assert.Equal(0, ((Avalonia.CornerRadius)radius!).TopLeft);

        engine.Apply(new ThemeSelection("breeze", VariantPreference.Dark, Color.Parse("#FF8800")));
        Assert.True(app.TryFindResource("HighlightBrush", Avalonia.Styling.ThemeVariant.Dark, out var highlight));
        Assert.Equal(Color.Parse("#FF8800"), ((ISolidColorBrush)highlight!).Color);
        Assert.True(app.TryFindResource("ThemeBackgroundBrush", Avalonia.Styling.ThemeVariant.Dark, out var window));
        Assert.Equal(Color.Parse("#31363B"), ((ISolidColorBrush)window!).Color);
        TestTheme.Reset();
    }
}
