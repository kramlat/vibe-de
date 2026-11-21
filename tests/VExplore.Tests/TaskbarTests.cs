using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vibe.Registry;
using VExplore.Windows;

namespace VExplore.Tests;

public class TaskbarTests
{
    private sealed class FakeWindows : IWindowSource
    {
        public List<WindowInfo> List { get; } = [];
        public List<string> Log { get; } = [];
        public IReadOnlyList<WindowInfo> Windows => List;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
        public void Activate(string id) => Log.Add("activate " + id);
        public void Minimize(string id) => Log.Add("minimize " + id);
        public void Close(string id) => Log.Add("close " + id);
    }

    private sealed class Fx : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vexplore-tb-" + Guid.NewGuid().ToString("N"));
        public DesktopEntryDatabase Apps { get; }
        public Hive Hive { get; } = new("HKCU") { Identity = RegistryIdentity.Root };
        public FakeWindows Windows { get; } = new();
        public List<string> Launched { get; } = [];
        public Fx()
        {
            Directory.CreateDirectory(Path.Combine(Dir, "applications"));
            foreach (var (id, name, extra) in new[] { ("kate", "Kate", ""), ("konsole", "Konsole", "StartupWMClass=org.kde.konsole\n"), ("okular", "Okular", "") })
                File.WriteAllText(Path.Combine(Dir, "applications", id + ".desktop"), $"[Desktop Entry]\nType=Application\nName={name}\nExec={id}\nIcon={id}\n{extra}");
            var env = new Dictionary<string, string> { ["HOME"] = Dir, ["XDG_DATA_HOME"] = Dir, ["XDG_DATA_DIRS"] = "/nonexistent" };
            Apps = DesktopEntryDatabase.Load(new XdgDirectories(k => env.GetValueOrDefault(k)), []);
        }
        public TaskbarModel Model() => new(Hive, Apps, Windows, e => Launched.Add(e.Id));
        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Fact]
    public void Pinned_apps_come_first_then_running_ones_and_windows_of_one_app_share_a_button()
    {
        using var f = new Fx();
        using var model = f.Model();
        model.Pin("kate.desktop");
        model.Pin("konsole.desktop");
        model.Pin("nothing.desktop");   // unknown apps cannot be pinned
        f.Windows.List.AddRange([new("1", "a.txt", "okular"), new("2", "~", "org.kde.konsole"), new("3", "~", "org.kde.konsole", IsActive: true), new("4", "mystery", "mystery")]);

        var buttons = model.Buttons();

        Assert.Equal(new[] { "Kate", "Konsole", "Okular", "mystery" }, buttons.Select(b => b.Name));
        Assert.False(buttons[0].IsRunning);
        Assert.Equal(2, buttons[1].Windows.Count);   // matched by StartupWMClass
        Assert.True(buttons[1].IsActive);
        Assert.False(buttons[3].Pinned);
        Assert.Null(buttons[3].App);

        model.Unpin("kate.desktop");
        Assert.Equal(new[] { "konsole.desktop" }, model.PinnedIds);
    }

    [Fact]
    public void Clicking_launches_raises_minimizes_or_asks_which_window()
    {
        using var f = new Fx();
        using var model = f.Model();
        model.Pin("kate.desktop");
        Assert.Equal(TaskbarClickResult.Launched, model.Click(model.Buttons()[0]));
        Assert.Equal(new[] { "kate.desktop" }, f.Launched);

        f.Windows.List.Add(new("1", "a", "kate"));
        Assert.Equal(TaskbarClickResult.Activated, model.Click(model.Buttons()[0]));
        f.Windows.List[0] = new("1", "a", "kate", IsActive: true);
        Assert.Equal(TaskbarClickResult.Minimized, model.Click(model.Buttons()[0]));
        f.Windows.List[0] = new("1", "a", "kate", IsActive: true, IsMinimized: true);
        Assert.Equal(TaskbarClickResult.Activated, model.Click(model.Buttons()[0]));   // minimized: bring it back
        f.Windows.List.Add(new("2", "b", "kate"));
        Assert.Equal(TaskbarClickResult.ChooseWindow, model.Click(model.Buttons()[0]));
        Assert.Equal(new[] { "activate 1", "minimize 1", "activate 1" }, f.Windows.Log);
    }

    [Fact]
    public void Edge_and_alignment_are_registry_settings_that_default_to_bottom_and_centre()
    {
        using var f = new Fx();
        using var model = f.Model();
        Assert.Equal(TaskbarEdge.Bottom, model.Edge);
        Assert.Equal(TaskbarAlignment.Center, model.Alignment);

        var changed = 0;
        model.Changed += (_, _) => changed++;
        model.Edge = TaskbarEdge.Right;
        model.Alignment = TaskbarAlignment.Start;

        Assert.True(model.IsVertical);
        Assert.Equal(TaskbarAlignment.Start, model.Alignment);
        Assert.True(changed >= 2);
        f.Hive.SetValue(TaskbarModel.KeyPath, "Edge", RegistryValueKind.String, "garbage");
        Assert.Equal(TaskbarEdge.Bottom, model.Edge);
    }

    [AvaloniaTheory]
    [InlineData(TaskbarEdge.Bottom, TaskbarAlignment.Center)]
    [InlineData(TaskbarEdge.Bottom, TaskbarAlignment.Start)]
    [InlineData(TaskbarEdge.Top, TaskbarAlignment.Center)]
    [InlineData(TaskbarEdge.Left, TaskbarAlignment.Start)]
    [InlineData(TaskbarEdge.Right, TaskbarAlignment.Center)]
    public void The_view_lays_out_along_every_edge_and_both_alignments(TaskbarEdge edge, TaskbarAlignment alignment)
    {
        using var f = new Fx();
        using var model = f.Model();
        model.Pin("kate.desktop"); model.Pin("konsole.desktop");
        f.Windows.List.Add(new("1", "~", "org.kde.konsole", IsActive: true));
        model.Edge = edge; model.Alignment = alignment;
        var window = new TaskbarWindow(model, () => { }, () => new DateTime(2026, 10, 16, 9, 5, 0)) { Width = edge is TaskbarEdge.Left or TaskbarEdge.Right ? 48 : 900, Height = edge is TaskbarEdge.Left or TaskbarEdge.Right ? 500 : 48 };
        window.Show();
        window.View.Rebuild();
        window.UpdateLayout();

        var start = window.View.StartButton.Bounds;
        var first = window.View.AppButtons[0];
        var origin = first.TranslatePoint(new Point(0, 0), window.View)!.Value;
        var startOrigin = window.View.StartButton.TranslatePoint(new Point(0, 0), window.View)!.Value;
        if (model.IsVertical)
        {
            Assert.True(origin.Y > startOrigin.Y && Math.Abs(origin.X - startOrigin.X) < 1);   // a column: apps below the start button
            Assert.True(alignment == TaskbarAlignment.Start ? startOrigin.Y < 20 : startOrigin.Y > 50);
        }
        else
        {
            Assert.True(origin.X > startOrigin.X && Math.Abs(origin.Y - startOrigin.Y) < 1);   // a row: apps after the start button
            Assert.True(alignment == TaskbarAlignment.Start ? startOrigin.X < 20 : startOrigin.X > 100);
        }
        Assert.Equal(2, window.View.AppButtons.Count);
        Assert.Contains("05", window.View.ClockText);

        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is { Length: > 0 } dir)
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"taskbar-{edge}-{alignment}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void Clicking_the_start_button_and_an_app_button_reach_the_shell()
    {
        using var f = new Fx();
        using var model = f.Model();
        model.Pin("kate.desktop");
        var started = 0;
        var window = new TaskbarWindow(model, () => started++);
        window.Show();

        window.View.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.View.AppButtons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, started);
        Assert.Equal(new[] { "kate.desktop" }, f.Launched);
    }

    [AvaloniaFact]
    public void The_search_task_view_and_keyboard_buttons_follow_their_settings_and_reach_the_shell()
    {
        using var f = new Fx();
        using var model = f.Model();
        var window = new TaskbarWindow(model, () => { });
        window.Show();
        var search = 0; var taskView = 0; var keyboard = 0;
        window.View.SearchInvoked += (_, _) => search++;
        window.View.TaskViewInvoked += (_, _) => taskView++;
        window.View.TouchKeyboardInvoked += (_, _) => keyboard++;

        // by default Search and Task view are on, the corner's keyboard icon is off
        Assert.True(window.View.SearchButton.IsVisible && window.View.SearchButton.GetVisualParent() is not null);
        Assert.True(window.View.TaskViewButton.GetVisualParent() is not null);
        Assert.Null(window.View.TouchKeyboardButton.GetVisualParent());

        model.ShowTouchKeyboard = true; model.ShowSearch = false;
        window.View.Rebuild();
        Assert.Null(window.View.SearchButton.GetVisualParent());
        Assert.NotNull(window.View.TouchKeyboardButton.GetVisualParent());

        window.View.TaskViewButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.View.TouchKeyboardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((0, 1, 1), (search, taskView, keyboard));
    }

    [AvaloniaTheory]
    [InlineData(StartLayout.Modern, 584)]   // wide enough for the six-across pinned tile grid
    [InlineData(StartLayout.Classic, 640)]
    public void The_start_menu_follows_the_layout_setting_and_both_styles_start_apps(StartLayout layout, int width)
    {
        using var f = new Fx();
        var launcher = new LauncherModel(f.Hive, f.Apps, ["VDE"]) { Layout = layout };
        launcher.Pin("kate.desktop");
        launcher.RecordLaunch("okular.desktop");
        var started = new List<string>();
        var window = new LauncherWindow(launcher, app => started.Add(app.Id), _ => { });
        window.Show();

        Assert.Equal(width, window.Width);
        var buttons = window.View.GetVisualDescendants().OfType<Button>().Select(b => b.Tag as string).ToList();
        Assert.Contains("Kate", buttons);                                    // pinned: a tile in both styles
        Assert.Equal(layout == StartLayout.Classic, buttons.Contains("Konsole"));   // classic lists every app up front
        Assert.Equal(layout == StartLayout.Modern, buttons.Count(b => b == "Okular") == 1 && !buttons.Contains("Konsole"));   // modern recommends the recent one

        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is { Length: > 0 } dir)
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"startmenu-{layout}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        window.View.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == "Kate").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "kate.desktop" }, started);
    }

    [Fact]
    public void Layout_taskbar_edge_and_alignment_have_settings_pages_that_write_the_same_registry_values()
    {
        using var f = new Fx();
        var layout = new SettingBinding(f.Hive, BuiltInPages.Find("startmenu")!.Settings[0]);
        Assert.Equal("Modern", layout.Read());
        Assert.True(layout.Write("Classic"));
        Assert.Equal(StartLayout.Classic, new LauncherModel(f.Hive, f.Apps, []).Layout);

        var edge = new SettingBinding(f.Hive, BuiltInPages.Find("taskbar")!.Settings.Single(s => s.Id == "edge"));
        Assert.True(edge.Write("Left"));
        using var model = f.Model();
        Assert.Equal(TaskbarEdge.Left, model.Edge);
        Assert.Contains(BuiltInPages.Items, i => i.PageId == "startmenu");
        Assert.Contains(BuiltInPages.Items, i => i.PageId == "taskbar");
    }
}
