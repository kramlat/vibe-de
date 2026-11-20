using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vde.Shell;
using Vibe.Registry;

namespace Vde.Shell.Controls.Tests;

public class LockScreenTests
{
    [Fact]
    public void Lock_screen_settings_round_trip_with_a_real_boolean_and_fall_back_on_junk()
    {
        var hive = new Hive("HKCU");
        Assert.Equal(new LockScreenSettings(), LockScreenSettingsStore.Load(hive));

        LockScreenSettingsStore.Save(hive, new LockScreenSettings(LockBackground.Color, "/p.png", "/pics", "#112233", "org.kde.kclock.desktop", false));
        var s = LockScreenSettingsStore.Load(hive);
        Assert.Equal((LockBackground.Color, "#112233", "org.kde.kclock.desktop", false), (s.Background, s.Color, s.StatusApp, s.ShowOnSignIn));
        Assert.Equal(RegistryValueKind.Bool, hive.GetValue(LockScreenSettingsStore.KeyPath, "ShowOnSignIn")!.Kind);

        hive.SetValue(LockScreenSettingsStore.KeyPath, "Color", RegistryValueKind.String, "red");
        hive.SetValue(LockScreenSettingsStore.KeyPath, "Background", RegistryValueKind.String, "Sepia");
        Assert.Equal(new LockScreenSettings { ShowOnSignIn = false, Picture = "/p.png", SlideshowFolder = "/pics", StatusApp = "org.kde.kclock.desktop" }.Color, LockScreenSettingsStore.Load(hive).Color);
        Assert.Equal(LockBackground.Picture, LockScreenSettingsStore.Load(hive).Background);
    }

    [Fact]
    public void The_picture_shown_is_the_chosen_one_else_the_wallpaper_and_a_slideshow_cycles_a_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vde-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in new[] { "b.png", "a.png", "notes.txt" }) File.WriteAllText(Path.Combine(dir, n), "x");
            var desktop = new DesktopSettings(Wallpaper: Path.Combine(dir, "a.png"));

            Assert.Equal(Path.Combine(dir, "a.png"), LockScreenSettingsStore.PictureToShow(new LockScreenSettings(), desktop));   // no own picture: the wallpaper
            Assert.Equal(Path.Combine(dir, "b.png"), LockScreenSettingsStore.PictureToShow(new LockScreenSettings(Picture: Path.Combine(dir, "b.png")), desktop));
            Assert.Null(LockScreenSettingsStore.PictureToShow(new LockScreenSettings(LockBackground.Color), desktop));
            var show = new LockScreenSettings(LockBackground.Slideshow, SlideshowFolder: dir);
            Assert.Equal(new[] { "a.png", "b.png", "a.png" }, new[] { 0, 1, 2 }.Select(i => Path.GetFileName(LockScreenSettingsStore.PictureToShow(show, desktop, i)!)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void The_lock_screen_view_shows_the_time_date_and_status()
    {
        var view = new LockScreenView(1, () => new DateTime(2021, 10, 6, 14, 56, 0));
        view.Apply(new LockScreenSettings(LockBackground.Color, Color: "#102030"), new DesktopSettings(), "Sunny, 18°");
        new Window { Content = view, Width = 800, Height = 600 }.Show();

        Assert.Equal("Wednesday, October 6", view.DateText);
        Assert.Contains("56", view.TimeText);
        Assert.Equal("Sunny, 18°", view.StatusText);
    }

    [AvaloniaFact]
    public void The_lock_screen_page_saves_choices_shows_the_right_options_and_links_to_related_pages()
    {
        var hive = new Hive("HKCU");
        var page = new LockScreenPageView(hive, [], [new SettingChoice("weather.desktop", "Weather")]);
        new Window { Content = page, Width = 900, Height = 900 }.Show();
        string? opened = null;
        page.OpenPage += (_, id) => opened = id;

        Assert.True(page.PictureChoicesVisible);
        page.ModeBox.SelectedIndex = 1;
        Assert.Equal(LockBackground.Slideshow, LockScreenSettingsStore.Load(hive).Background);
        Assert.True(page.FolderChoiceVisible);
        Assert.False(page.PictureChoicesVisible);

        page.SetMode(LockBackground.Color);
        Assert.False(page.SetColor("blue"));
        Assert.NotNull(page.ColorError);
        Assert.True(page.SetColor("#abcdef"));
        Assert.Equal("#ABCDEF", LockScreenSettingsStore.Load(hive).Color);

        page.StatusBox.SelectedIndex = 1;
        Assert.Equal("weather.desktop", LockScreenSettingsStore.Load(hive).StatusApp);
        page.StatusBox.SelectedIndex = 0;
        Assert.Null(LockScreenSettingsStore.Load(hive).StatusApp);

        page.SignInToggle.IsChecked = false;
        Assert.False(LockScreenSettingsStore.Load(hive).ShowOnSignIn);

        var links = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(page).OfType<Button>().Where(b => ((string?)b.Tag)?.StartsWith("link:") == true).ToList();
        Assert.Equal(new[] { "link:power", "link:screensaver" }, links.Select(b => (string)b.Tag!));
        links[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("power", opened);
    }

    [Fact]
    public void The_screen_timeout_and_screen_saver_pages_store_whole_numbers_as_Int32_and_flags_as_Bool()
    {
        var hive = new Hive("HKCU");
        var off = new SettingBinding(hive, BuiltInPages.Power.Settings.Single(s => s.Id == "screenoff"));
        var lockOn = new SettingBinding(hive, BuiltInPages.Power.Settings.Single(s => s.Id == "lock"));
        Assert.Equal(10L, off.Read());
        Assert.True(off.Write(15L));
        Assert.False(off.Write(5000L));
        Assert.True(lockOn.Write(false));
        Assert.Equal(RegistryValueKind.Int32, hive.GetValue(BuiltInPages.PowerKey, "ScreenOffMinutes")!.Kind);
        Assert.Equal(RegistryValueKind.Bool, hive.GetValue(BuiltInPages.PowerKey, "LockOnScreenOff")!.Kind);
        Assert.Equal(15L, off.Read());
        hive.SetValue(BuiltInPages.PowerKey, "ScreenOffMinutes", RegistryValueKind.DWord, 20u);   // written by older versions or other tools
        Assert.Equal(20L, off.Read());
        Assert.NotNull(BuiltInPages.Find("screensaver"));
    }
}
