using Avalonia.Headless;
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

        Assert.Equal(new[] { "fluent", "simple", "breeze", "redmond", "redmond98" }, page.Tiles.Select(t => (string)t.Tag!));
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

public class CaptionButtonTests
{
    private static (byte R, byte G, byte B) Pixel(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);   
        return (bytes[0], bytes[1], bytes[2]);   // this frame is RGBA
    }

    private static (byte R, byte G, byte B) CornerOf(CaptionButton button, string style, VariantPreference variant)
    {
        TestTheme.Engine.Apply(new ThemeSelection(style, variant));
        var window = new Window { Width = 100, Height = 60, Content = new Border { Width = 46, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = button } };
        window.Show();
        var pixel = Pixel(window, 3, 3);   // inside the button, away from the symbol
        window.Close();
        return pixel;
    }

    [AvaloniaFact]
    public void Close_is_red_under_the_pointer_in_every_style_but_Redmond_has_no_hover_colour()
    {
        foreach (var (style, variant) in new[] { ("fluent", VariantPreference.Light), ("fluent", VariantPreference.Dark), ("simple", VariantPreference.Light), ("breeze", VariantPreference.Dark) })
        {
            var (r, g, b) = CornerOf(new CaptionButton(CaptionKind.Close) { IsHot = true }, style, variant);
            Assert.Equal((0xE8, 0x11, 0x23), (r, g, b));   // red, not grey, whatever the theme calls critical
        }
        foreach (var style in new[] { "redmond", "redmond98" })
        {
            var hot = CornerOf(new CaptionButton(CaptionKind.Close) { IsHot = true }, style, VariantPreference.Light);
            var idle = CornerOf(new CaptionButton(CaptionKind.Close), style, VariantPreference.Light);
            Assert.Equal(idle, hot);   // nothing changes under the pointer in the classic look
            Assert.NotEqual(((byte)0xE8, (byte)0x11, (byte)0x23), hot);
        }
        TestTheme.Reset();
    }

    [AvaloniaFact]
    public void Every_window_uses_the_same_button_and_a_click_raises_Click_only_inside_it()
    {
        var button = new CaptionButton(CaptionKind.Close);
        Assert.Equal(46, button.Width);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = new Window { Width = 100, Height = 60, Content = new Border { Width = 46, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = button } };
        window.Show();
        window.MouseMove(new Avalonia.Point(20, 10));
        Assert.True(button.IsHot);
        window.MouseDown(new Avalonia.Point(20, 10), Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(20, 10), Avalonia.Input.MouseButton.Left);
        Assert.Equal(1, clicks);
        window.MouseDown(new Avalonia.Point(20, 10), Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(80, 50), Avalonia.Input.MouseButton.Left);   // let go outside: not a click
        Assert.Equal(1, clicks);
    }
}
