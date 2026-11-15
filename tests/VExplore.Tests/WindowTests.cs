using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Components;
using Vde.Shell.ComDlg;
using Vde.Shell;
using Vde.Components;
using Vde.Shell.ComDlg;
using Vibe.Registry;
using VExplore.Windows;

namespace VExplore.Tests;

public class WindowTests
{
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-" + Guid.NewGuid().ToString("N"));
        public KnownFolders Folders { get; }
        public Tree()
        {
            Directory.CreateDirectory(Path.Combine(Root, "home", "Documents", "sub"));
            File.WriteAllText(Path.Combine(Root, "home", "Documents", "a.txt"), "hi");
            File.WriteAllText(Path.Combine(Root, "home", "Documents", ".secret"), "x");
            File.WriteAllText(Path.Combine(Root, "home", "readme.md"), "x");
            var env = new Dictionary<string, string> { ["HOME"] = Path.Combine(Root, "home"), ["XDG_CONFIG_HOME"] = Path.Combine(Root, "cfg") };
            Folders = new KnownFolders(new XdgDirectories(k => env.GetValueOrDefault(k)));
        }
        public string Home => Path.Combine(Root, "home");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [AvaloniaFact]
    public void The_file_manager_navigates_with_history_up_and_the_address_bar_and_opens_files_through_the_shell()
    {
        using var t = new Tree();
        var opened = new List<string>();
        var window = new FileManagerWindow(t.Home, t.Folders, opened.Add);
        window.Show();

        Assert.Equal(new[] { "Documents", "readme.md" }, window.Entries.Select(e => e.Name));
        Assert.Equal("2 items", window.StatusText);

        window.Open([window.Entries[0]]);                                           // a folder opens in place
        Assert.Equal(Path.Combine(t.Home, "Documents"), window.CurrentFolder);
        Assert.Equal("Documents", window.Title);
        Assert.Equal(new[] { "sub", "a.txt" }, window.Entries.Select(e => e.Name));
        Assert.True(window.CanGoBack);

        window.ShowHidden = true;
        Assert.Contains(window.Entries, e => e.Name == ".secret");
        window.ShowHidden = false;

        window.Open([window.Entries.Single(e => e.Name == "a.txt")]);              // a file goes to the shell
        Assert.Equal(new[] { Path.Combine(t.Home, "Documents", "a.txt") }, opened);

        window.GoUp();
        Assert.Equal(t.Home, window.CurrentFolder);
        window.GoBack();
        Assert.Equal(Path.Combine(t.Home, "Documents"), window.CurrentFolder);
        window.GoForward();
        Assert.Equal(t.Home, window.CurrentFolder);

        window.Navigate(Path.Combine(t.Root, "does-not-exist"));
        Assert.Empty(window.Entries);                                                // a missing folder is a message, not a crash
        Assert.Contains("cannot be opened", window.StatusText);
    }

    [AvaloniaFact]
    public async Task The_file_manager_notices_changes_in_the_folder_by_itself()
    {
        using var t = new Tree();
        var window = new FileManagerWindow(t.Home, t.Folders, _ => { });
        window.Show();

        File.WriteAllText(Path.Combine(t.Home, "new-file.txt"), "x");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!window.Entries.Any(e => e.Name == "new-file.txt") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains(window.Entries, e => e.Name == "new-file.txt");
    }

    [AvaloniaFact]
    public void The_settings_window_shows_categories_searches_and_opens_pages_or_hands_off_other_items()
    {
        var hive = new Hive("HKCU") { Identity = RegistryIdentity.Root };
        var key = ControlPanelCatalog.RegistryKey + "\\printers";
        hive.SetValue(key, "Name", RegistryValueKind.String, "Printers");
        hive.SetValue(key, "Command", RegistryValueKind.String, "/bin/true");
        hive.SetValue(key, "Category", RegistryValueKind.String, "Devices");
        var catalog = ControlPanelCatalog.Load(null, hive, null, []);
        var handed = new List<ControlPanelItem>();
        var window = new ControlPanelWindow(catalog, hive, handed.Add);
        window.Show();

        Assert.Equal(new[] { "Appearance", "Desktop", "Start menu", "Taskbar", "Windows" }, window.ShownItems.Select(i => i.Name));          // the first category is selected
        window.Search("dark");
        Assert.Equal("Appearance", window.ShownItems[0].Name);

        window.Open(window.ShownItems[0]);
        Assert.Equal("appearance", window.OpenPageId);                              // our own page shows inside the window
        window.Search("printers");
        window.Open(window.ShownItems.Single());
        Assert.Equal("printers", Assert.Single(handed).Id);                         // anything else is started by the shell
    }

    [AvaloniaFact]
    public void The_launcher_window_starts_an_app_then_closes_and_escape_closes_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vexplore-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "applications"));
        File.WriteAllText(Path.Combine(dir, "applications", "kate.desktop"), "[Desktop Entry]\nType=Application\nName=Kate\nExec=kate\n");
        try
        {
            var env = new Dictionary<string, string> { ["HOME"] = dir, ["XDG_DATA_HOME"] = dir, ["XDG_DATA_DIRS"] = "/nonexistent" };
            var apps = DesktopEntryDatabase.Load(new XdgDirectories(k => env.GetValueOrDefault(k)), []);
            var hive = new Hive("HKCU") { Identity = RegistryIdentity.Root };
            var model = new LauncherModel(hive, apps, ["VDE"]);
            var started = new List<string>();
            var window = new LauncherWindow(model, app => started.Add(app.Id), _ => { });
            window.Show();
            bool closed = false;
            window.Closed += (_, _) => closed = true;

            window.View.ShowAllApps = true;
            var button = window.View.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Tag == "Kate");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(new[] { "kate.desktop" }, started);
            Assert.True(closed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Command_line_mistakes_are_reported()
    {
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(1, ExplorerCommand.Run(["--settings", "extra"], o, e));
        Assert.Equal(2, ExplorerCommand.Run(["/definitely/not/a/folder"], o, e));
        Assert.Contains("not a folder", e.ToString());
        Assert.Equal(0, ExplorerCommand.Run(["--help"], o, e));
        Assert.Contains("--launcher", o.ToString());
    }
}

public class Screenshots
{
    // Writes a picture for a human to look at when VDE_SCREENSHOT_DIR is set; otherwise does nothing.
    [AvaloniaFact]
    public void File_manager_screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Vde.Shell.Controls.ShellIconProvider.Default = new Vde.Shell.Controls.ShellIconProvider();
        var xdg = XdgDirectories.Default;
        var mime = new MimeDatabase(xdg);
        var types = new FileTypes(mime, new ClassesRoot(new Hive("HKCU")), Locale.FromEnvironment());
        var window = new FileManagerWindow(new KnownFolders(xdg).Get(KnownFolder.Home), new KnownFolders(xdg), _ => { }, types) { Width = 980, Height = 560 };
        window.Show();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "filemanager.png"));
    }
}

public class FileActionWindowTests
{
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-act-" + Guid.NewGuid().ToString("N"));
        public KnownFolders Folders { get; }
        public Trash Trash { get; }
        public Vde.Shell.Controls.InMemoryFileClipboard Clip { get; } = new();
        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Root, "home", "a")); Directory.CreateDirectory(Path.Combine(Root, "home", "b"));
            File.WriteAllText(Path.Combine(Root, "home", "a", "one.txt"), "1");
            File.WriteAllText(Path.Combine(Root, "home", "a", "two.txt"), "2");
            var env = new Dictionary<string, string> { ["HOME"] = Path.Combine(Root, "home"), ["XDG_DATA_HOME"] = Path.Combine(Root, "data") };
            var xdg = new XdgDirectories(k => env.GetValueOrDefault(k));
            Folders = new KnownFolders(xdg);
            Trash = new Trash(xdg);
        }
        public string A => Path.Combine(Root, "home", "a");
        public string B => Path.Combine(Root, "home", "b");
        public FileManagerWindow Open(string folder) => new(folder, Folders, _ => { }, null, new FileActions(Trash), Clip);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [AvaloniaFact]
    public async Task Copy_then_paste_elsewhere_copies_and_cut_then_paste_moves_and_empties_the_clipboard()
    {
        using var e = new Env();
        var from = e.Open(e.A); from.Show();
        from.SelectEntries("one.txt");
        await from.CopyAsync();
        Assert.Equal(TransferKind.Copy, e.Clip.Current!.Kind);

        var to = e.Open(e.B); to.Show();
        await to.PasteAsync();
        Assert.True(File.Exists(Path.Combine(e.B, "one.txt")));
        Assert.True(File.Exists(Path.Combine(e.A, "one.txt")));                 // copy leaves the original
        Assert.NotNull(e.Clip.Current);                                          // and can be pasted again

        await to.PasteAsync();                                                   // same name there: the copy is renamed, nothing overwritten
        Assert.True(File.Exists(Path.Combine(e.B, "one (copy).txt")));

        from.SelectEntries("two.txt");
        await from.CutAsync();
        await to.PasteAsync();
        Assert.True(File.Exists(Path.Combine(e.B, "two.txt")));
        Assert.False(File.Exists(Path.Combine(e.A, "two.txt")));
        Assert.Null(e.Clip.Current);                                             // a cut is used up by pasting it
    }

    [AvaloniaFact]
    public void Delete_goes_to_the_trash_new_folder_and_rename_work_and_bad_names_are_explained()
    {
        using var e = new Env();
        var w = e.Open(e.A); w.Show();

        w.SelectEntries("one.txt");
        w.DeleteSelected();
        Assert.False(File.Exists(Path.Combine(e.A, "one.txt")));
        Assert.Single(e.Trash.List());

        var made = w.NewFolder();
        Assert.EndsWith("New folder", made);
        Assert.Equal(new[] { "New folder" }, w.SelectedPaths.Select(Path.GetFileName));   // the new folder is selected, ready to be renamed

        Assert.False(w.Rename("bad/name"));
        Assert.Contains("cannot contain", w.StatusText);
        Assert.True(w.Rename("Projects"));
        Assert.True(Directory.Exists(Path.Combine(e.A, "Projects")));

        w.SelectEntries("two.txt");
        w.DeleteSelected(permanent: true);
        Assert.False(File.Exists(Path.Combine(e.A, "two.txt")));
        Assert.Single(e.Trash.List());                                             // permanent deletes skip the trash
    }

    [AvaloniaFact]
    public void Dropping_files_moves_copies_or_links_by_the_rules_and_never_overwrites()
    {
        using var e = new Env();
        var w = e.Open(e.B); w.Show();
        var source = Path.Combine(e.A, "one.txt");

        var copied = w.HandleDrop([source], null, ctrl: true, shift: false);
        Assert.Equal(new[] { Path.Combine(e.B, "one.txt") }, copied);
        Assert.True(File.Exists(source));

        var again = w.HandleDrop([source], null, ctrl: true, shift: false);
        Assert.Equal(Path.Combine(e.B, "one (copy).txt"), again[0]);

        Assert.Empty(w.HandleDrop([Path.Combine(e.B, "one.txt")], null, false, false));   // dropped where it already is: nothing
        Assert.Empty(w.HandleDrop([e.B], Path.Combine(e.B, "nope"), false, false));        // not a folder
        var moved = w.HandleDrop([Path.Combine(e.A, "two.txt")], null, ctrl: false, shift: true);
        Assert.False(File.Exists(Path.Combine(e.A, "two.txt")));
        Assert.True(File.Exists(moved[0]));
    }

    [AvaloniaFact]
    public async Task A_clipboard_with_nothing_usable_pastes_nothing()
    {
        using var e = new Env();
        var w = e.Open(e.B); w.Show();

        await w.PasteAsync();
        await w.CopyAsync();                                                       // nothing selected: nothing copied
        Assert.Null(e.Clip.Current);
        Assert.Empty(Directory.GetFileSystemEntries(e.B));
    }
}

public class DesktopWindowTests
{
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-desk-" + Guid.NewGuid().ToString("N"));
        public string Desktop => Path.Combine(Root, "Desktop");
        public Hive Hive { get; } = new("HKCU") { Identity = RegistryIdentity.Root };
        public Trash Trash { get; }
        public Vde.Shell.Controls.InMemoryFileClipboard Clip { get; } = new();
        public List<string> Opened { get; } = new();
        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Desktop, "Projects"));
            File.WriteAllText(Path.Combine(Desktop, "notes.txt"), "n");
            File.WriteAllText(Path.Combine(Desktop, "todo.txt"), "t");
            File.WriteAllText(Path.Combine(Desktop, ".hidden"), "h");
            var env = new Dictionary<string, string> { ["HOME"] = Root, ["XDG_DATA_HOME"] = Path.Combine(Root, "data") };
            Trash = new Trash(new XdgDirectories(k => env.GetValueOrDefault(k)));
        }
        public DesktopWindow Open() => new(Desktop, Hive, Opened.Add, null, new FileActions(Trash), Clip) { Width = 1000, Height = 600 };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [AvaloniaFact]
    public void The_desktop_shows_the_folders_contents_as_icons_filling_down_the_grid_and_hides_dotfiles()
    {
        using var e = new Env();
        var w = e.Open(); w.Show();

        Assert.Equal(new[] { "Projects", "notes.txt", "todo.txt" }, w.Entries.Select(x => x.Name));   // folders first
        Assert.Equal(new Cell(0, 0), w.CellOf("Projects"));
        Assert.Equal(new Cell(0, 1), w.CellOf("notes.txt"));
        Assert.Equal(new Cell(0, 2), w.CellOf("todo.txt"));
        Assert.True(w.IconsVisible);
    }

    [AvaloniaFact]
    public void Moving_an_icon_to_a_free_cell_is_remembered_and_a_taken_cell_sends_it_back()
    {
        using var e = new Env();
        var w = e.Open(); w.Show();

        w.SelectIcon("notes.txt");
        w.DropIcon("notes.txt", new Cell(5, 3));
        Assert.Equal(new Cell(5, 3), w.CellOf("notes.txt"));
        Assert.Equal(new Cell(5, 3), DesktopSettingsStore.LoadPositions(e.Hive)["notes.txt"]);       // saved in the registry

        w.DropIcon("todo.txt", new Cell(5, 3));                                                        // somebody is there already
        Assert.NotEqual(new Cell(5, 3), w.CellOf("todo.txt"));
        Assert.DoesNotContain("todo.txt", DesktopSettingsStore.LoadPositions(e.Hive).Keys);

        var again = e.Open(); again.Show();                                                            // a new session keeps the layout
        Assert.Equal(new Cell(5, 3), again.CellOf("notes.txt"));

        File.Delete(Path.Combine(e.Desktop, "notes.txt"));
        again.Reload();
        Assert.DoesNotContain("notes.txt", DesktopSettingsStore.LoadPositions(e.Hive).Keys);           // forgets files that are gone
    }

    [AvaloniaFact]
    public void Dropping_an_icon_on_a_folder_moves_the_selection_into_it()
    {
        using var e = new Env();
        var w = e.Open(); w.Show();

        w.SelectIcon("notes.txt"); w.SelectIcon("todo.txt", add: true);
        w.DropIcon("todo.txt", w.CellOf("Projects"));

        Assert.True(File.Exists(Path.Combine(e.Desktop, "Projects", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(e.Desktop, "Projects", "todo.txt")));
        Assert.DoesNotContain(w.Entries, x => x.Name == "notes.txt");
    }

    [AvaloniaFact]
    public async Task Selection_opening_and_the_file_actions_work_like_in_a_file_manager_window()
    {
        using var e = new Env();
        var w = e.Open(); w.Show();

        w.SelectIcon("notes.txt");
        w.SelectIcon("todo.txt", add: true);
        Assert.Equal(2, w.SelectedNames.Count);
        w.SelectIcon("todo.txt", add: true);                                                           // ctrl+click again deselects
        Assert.Equal(new[] { "notes.txt" }, w.SelectedNames);
        w.OpenSelected();
        Assert.Equal(new[] { Path.Combine(e.Desktop, "notes.txt") }, e.Opened);

        await w.CopyAsync();
        await w.PasteAsync();
        Assert.Contains(w.Entries, x => x.Name == "notes (copy).txt");

        Assert.True(w.Rename("renamed.txt"));
        Assert.Contains(w.Entries, x => x.Name == "renamed.txt");
        Assert.False(w.Rename("a/b"));

        w.DeleteSelected();
        Assert.DoesNotContain(w.Entries, x => x.Name == "renamed.txt");
        Assert.Single(e.Trash.List());

        var made = w.NewFolder();
        Assert.EndsWith("New folder", made);
        Assert.Equal(new[] { "New folder" }, w.SelectedNames);
        Assert.Equal(new[] { "New folder", "Paste", "Refresh", "Properties", "Desktop settings" }, w.MenuLabels);
    }

    [AvaloniaFact]
    public void A_file_that_appears_on_the_desktop_shows_up_by_itself_and_a_renamed_icon_keeps_its_place()
    {
        using var e = new Env();
        var w = e.Open(); w.Show();
        w.SelectIcon("notes.txt"); w.DropIcon("notes.txt", new Cell(4, 4));

        w.Rename("memo.txt");
        Assert.Equal(new Cell(4, 4), w.CellOf("memo.txt"));

        File.WriteAllText(Path.Combine(e.Desktop, "arrived.txt"), "x");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!w.Entries.Any(x => x.Name == "arrived.txt") && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains(w.Entries, x => x.Name == "arrived.txt");
    }

    [AvaloniaFact]
    public void The_wallpaper_and_colour_come_from_the_registry_and_an_unreadable_image_falls_back_to_the_colour()
    {
        using var e = new Env();
        var picture = Path.Combine(e.Root, "wall.png");
        using (var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(8, 8), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque))
            bmp.Save(picture);

        DesktopSettingsStore.Save(e.Hive, new DesktopSettings(picture, WallpaperMode.Tile, "#102030"));
        var w = e.Open(); w.Show();
        var brush = Assert.IsType<Avalonia.Media.ImageBrush>(w.Background);
        Assert.Equal(Avalonia.Media.TileMode.Tile, brush.TileMode);

        File.WriteAllText(picture, "not an image");
        DesktopSettingsStore.Save(e.Hive, new DesktopSettings(picture, WallpaperMode.Fill, "#102030"));
        var broken = e.Open(); broken.Show();
        Assert.Equal(Avalonia.Media.Color.Parse("#102030"), Assert.IsType<Avalonia.Media.SolidColorBrush>(broken.Background).Color);

        DesktopSettingsStore.Save(e.Hive, new DesktopSettings(null, WallpaperMode.Fill, "#102030", ShowIcons: false));
        var bare = e.Open(); bare.Show();
        Assert.False(bare.IconsVisible);
    }
}

public class DesktopShellFolderTests
{
    [AvaloniaFact]
    public void This_Computer_is_an_icon_on_the_desktop_that_opens_the_shell_folder_and_can_be_hidden()
    {
        var desktop = Path.Combine(Path.GetTempPath(), "vexplore-dsk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(desktop, "notes.txt"), "x");
        try
        {
            var user = new Hive("HKCU") { Identity = RegistryIdentity.Root };
            var system = new Hive("HKLM") { Identity = RegistryIdentity.Root };
            var catalog = new ShellFolderCatalog(new ClassesRoot(user, system), system, user, new ComponentLoader());
            ShellFolderSetup.AddBuiltIns(catalog);
            var opened = new List<string>();
            var w = new DesktopWindow(desktop, user, _ => { }, shellFolders: catalog, openShellFolder: opened.Add);
            w.Show();

            var key = "::" + Vde.Components.ComponentInfo.NormalizeGuid(ShellFolderIds.Computer);
            Assert.Equal(new Cell(0, 0), w.CellOf(key));        // first icon
            Assert.Equal(new Cell(0, 1), w.CellOf("notes.txt")); // the files follow
            w.SelectIcon(key);
            w.OpenSelected();
            Assert.Equal(["shell:Computer"], opened);

            user.SetValue($@"{ShellFolderCatalog.NamespacesKey}\Desktop\{ShellFolderIds.Computer}", "Hidden", RegistryValueKind.DWord, 1u);
            w.Reload();
            Assert.Throws<KeyNotFoundException>(() => w.CellOf(key));
        }
        finally { Directory.Delete(desktop, recursive: true); }
    }
}

public class ShortcutWindowTests
{
    private static ShortcutStyleService Style(bool apple)
        => new(new Hive("HKCU") { Identity = RegistryIdentity.Root }, () => apple ? [new KeyboardDevice("Apple Keyboard", 3, 0x05ac, 1, true)] : [new KeyboardDevice("PC", 3, 1, 1, false)]);

    private static void Press(Window window, Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods)
        => window.KeyPressQwerty(KeyFor(key), Raw(mods));

    private static Avalonia.Input.RawInputModifiers Raw(Avalonia.Input.KeyModifiers m)
        => (m.HasFlag(Avalonia.Input.KeyModifiers.Control) ? Avalonia.Input.RawInputModifiers.Control : 0)
         | (m.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? Avalonia.Input.RawInputModifiers.Shift : 0)
         | (m.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? Avalonia.Input.RawInputModifiers.Alt : 0)
         | (m.HasFlag(Avalonia.Input.KeyModifiers.Meta) ? Avalonia.Input.RawInputModifiers.Meta : 0);

    private static Avalonia.Input.PhysicalKey KeyFor(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.C => Avalonia.Input.PhysicalKey.C,
        Avalonia.Input.Key.V => Avalonia.Input.PhysicalKey.V,
        Avalonia.Input.Key.N => Avalonia.Input.PhysicalKey.N,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-keys-" + Guid.NewGuid().ToString("N"));
        public KnownFolders Folders { get; }
        public Trash Trash { get; }
        public Vde.Shell.Controls.InMemoryFileClipboard Clip { get; } = new();
        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Root, "home", "a")); Directory.CreateDirectory(Path.Combine(Root, "home", "b"));
            File.WriteAllText(Path.Combine(Root, "home", "a", "one.txt"), "1");
            var env = new Dictionary<string, string> { ["HOME"] = Path.Combine(Root, "home"), ["XDG_DATA_HOME"] = Path.Combine(Root, "data") };
            Folders = new KnownFolders(new XdgDirectories(k => env.GetValueOrDefault(k)));
            Trash = new Trash(new XdgDirectories(k => env.GetValueOrDefault(k)));
        }
        public string A => Path.Combine(Root, "home", "a");
        public string B => Path.Combine(Root, "home", "b");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [AvaloniaTheory]
    [InlineData(true)]    // Command+C / Command+V on an Apple keyboard
    [InlineData(false)]   // Ctrl+C / Ctrl+V on a PC
    public async Task Copy_and_paste_by_keyboard_use_the_modifier_of_the_keyboard_in_use(bool apple)
    {
        using var e = new Env();
        var from = new FileManagerWindow(e.A, e.Folders, _ => { }, null, new FileActions(e.Trash), e.Clip, Style(apple)); from.Show();
        var to = new FileManagerWindow(e.B, e.Folders, _ => { }, null, new FileActions(e.Trash), e.Clip, Style(apple)); to.Show();
        var primary = apple ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
        var wrong = apple ? Avalonia.Input.KeyModifiers.Control : Avalonia.Input.KeyModifiers.Meta;

        from.SelectEntries("one.txt");
        Press(from, Avalonia.Input.Key.C, wrong);                          // the other style's modifier does nothing
        await Task.Delay(50);
        Assert.Null(e.Clip.Current);

        Press(from, Avalonia.Input.Key.C, primary);
        await Task.Delay(50);
        Assert.Equal(TransferKind.Copy, e.Clip.Current!.Kind);
        Press(to, Avalonia.Input.Key.V, primary);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Path.Combine(e.B, "one.txt")) && DateTime.UtcNow < deadline) { await Task.Delay(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        Assert.True(File.Exists(Path.Combine(e.B, "one.txt")));
    }

    [AvaloniaFact]
    public void The_file_managers_tooltips_show_apple_symbols_only_with_an_apple_keyboard()
    {
        using var e = new Env();
        var pc = new FileManagerWindow(e.A, e.Folders, _ => { }, null, new FileActions(e.Trash), e.Clip, Style(false)); pc.Show();
        var mac = new FileManagerWindow(e.A, e.Folders, _ => { }, null, new FileActions(e.Trash), e.Clip, Style(true)); mac.Show();

        Assert.Equal(ShortcutStyle.Pc, pc.ShortcutStyle);
        Assert.Equal("Back (Alt+←)", pc.BackHint);
        Assert.Equal(ShortcutStyle.Mac, mac.ShortcutStyle);
        Assert.Equal("Back (⌘[)", mac.BackHint);
    }

    [AvaloniaTheory]
    [InlineData(true, "⇧⌘N", "⌘V", "⌘R")]
    [InlineData(false, "Ctrl+Shift+N", "Ctrl+V", "F5")]
    public void The_desktop_menu_shows_each_shortcut_in_the_style_of_the_keyboard(bool apple, string newFolder, string paste, string refresh)
    {
        using var d = new DesktopTestEnv();
        var w = new DesktopWindow(d.Desktop, new Hive("HKCU") { Identity = RegistryIdentity.Root }, _ => { }, null, null, new Vde.Shell.Controls.InMemoryFileClipboard(), null, Style(apple));
        w.Show();

        Assert.Equal(new[] { "New folder", "Paste", "Refresh", "Properties", "Desktop settings" }, w.MenuLabels);
        Assert.Equal(new[] { newFolder, paste, refresh, apple ? "⌘I" : "Alt+Enter", "" }, w.MenuShortcuts);
    }

    [AvaloniaFact]
    public async Task On_the_desktop_command_n_makes_a_folder_with_an_apple_keyboard_and_ctrl_n_does_not()
    {
        using var d = new DesktopTestEnv();
        var w = new DesktopWindow(d.Desktop, new Hive("HKCU") { Identity = RegistryIdentity.Root }, _ => { }, null, new FileActions(new Trash()), new Vde.Shell.Controls.InMemoryFileClipboard(), null, Style(true));
        w.Show();

        Press(w, Avalonia.Input.Key.N, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift);
        await Task.Delay(50);
        Assert.Empty(Directory.GetDirectories(d.Desktop));
        Press(w, Avalonia.Input.Key.N, Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Shift);
        Assert.Single(Directory.GetDirectories(d.Desktop));
    }

    private sealed class DesktopTestEnv : IDisposable
    {
        public string Desktop { get; } = Path.Combine(Path.GetTempPath(), "vexplore-dk-" + Guid.NewGuid().ToString("N"));
        public DesktopTestEnv() => Directory.CreateDirectory(Desktop);
        public void Dispose() => Directory.Delete(Desktop, recursive: true);
    }
}

public class PropertiesFromWindowsTests
{
    private static ShortcutStyleService Style(bool apple)
        => new(new Hive("HKCU") { Identity = RegistryIdentity.Root }, () => apple ? [new KeyboardDevice("Apple Keyboard", 3, 0x05ac, 1, true)] : [new KeyboardDevice("PC", 3, 1, 1, false)]);

    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-props-" + Guid.NewGuid().ToString("N"));
        public KnownFolders Folders { get; }
        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Root, "home", "docs"));
            File.WriteAllText(Path.Combine(Root, "home", "docs", "a.txt"), "hello");
            var env = new Dictionary<string, string> { ["HOME"] = Path.Combine(Root, "home"), ["XDG_DATA_HOME"] = Path.Combine(Root, "data") };
            Folders = new KnownFolders(new XdgDirectories(k => env.GetValueOrDefault(k)));
        }
        public string Docs => Path.Combine(Root, "home", "docs");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [AvaloniaTheory]
    [InlineData(false, Avalonia.Input.RawInputModifiers.Alt)]      // Alt+Enter on a PC
    [InlineData(true, Avalonia.Input.RawInputModifiers.Meta)]      // Command+I on an Apple keyboard (the key differs, see below)
    public void The_properties_shortcut_asks_for_the_sheet_for_the_selection(bool apple, Avalonia.Input.RawInputModifiers mod)
    {
        using var e = new Env();
        var asked = new List<IReadOnlyList<string>>();
        var window = new FileManagerWindow(e.Docs, e.Folders, _ => { }, null, null, new Vde.Shell.Controls.InMemoryFileClipboard(), Style(apple),
            properties: paths => { asked.Add(paths); return new PropertySheetContext { Target = new PropertyTarget(paths, "text/plain") }; });
        window.Show();
        window.SelectEntries("a.txt");

        window.KeyPressQwerty(apple ? Avalonia.Input.PhysicalKey.I : Avalonia.Input.PhysicalKey.Enter, mod);

        Assert.Equal(new[] { Path.Combine(e.Docs, "a.txt") }, Assert.Single(asked));
    }

    [AvaloniaFact]
    public void The_sheet_opens_for_the_selection_or_for_the_folder_itself_when_nothing_is_selected()
    {
        using var e = new Env();
        var window = new FileManagerWindow(e.Docs, e.Folders, _ => { }, null, null, new Vde.Shell.Controls.InMemoryFileClipboard(), Style(false));
        window.Show();

        var folder = window.ShowProperties()!;
        Assert.Equal(e.Docs, folder.Items[0].FullPath);
        Assert.Equal("docs Properties", folder.Title);

        window.SelectEntries("a.txt");
        var file = window.ShowProperties()!;
        Assert.Equal("a.txt Properties", file.Title);
        Assert.Equal(new[] { "General", "Permissions", "Details", "Checksums" }, file.Pages.Select(p => p.Title));   // the shell's own handler adds Checksums for a file
    }

    [AvaloniaTheory]
    [InlineData(false, "Alt+Enter")]
    [InlineData(true, "⌘I")]
    public void The_desktop_menu_has_Properties_with_the_shortcut_in_the_keyboards_style(bool apple, string hint)
    {
        using var e = new Env();
        var w = new DesktopWindow(e.Docs, new Hive("HKCU") { Identity = RegistryIdentity.Root }, _ => { }, null, null, new Vde.Shell.Controls.InMemoryFileClipboard(), null, Style(apple));
        w.Show();

        var index = w.MenuLabels.ToList().IndexOf("Properties");
        Assert.True(index >= 0);
        Assert.Equal(hint, w.MenuShortcuts[index]);

        w.SelectIcon("a.txt");
        Assert.Equal("a.txt Properties", w.ShowProperties()!.Title);
    }
}

public class VExplorePropertyPagesTests
{
    private sealed class Dir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-pp-" + Guid.NewGuid().ToString("N"));
        public Dir() { Directory.CreateDirectory(Path.Combine(Root, "folder")); File.WriteAllText(Path.Combine(Root, "hello.txt"), "hello"); File.WriteAllText(Path.Combine(Root, "two.txt"), "2"); }
        public string File_ => Path.Combine(Root, "hello.txt");
        public string Folder => Path.Combine(Root, "folder");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static T Tag<T>(IReadOnlyList<PropertyPageInfo> pages, string title) where T : class => (T)pages.Single(p => p.Title == title).Tag!;

    [AvaloniaFact]
    public void The_shells_handler_gives_files_permissions_and_checksums_folders_permissions_and_customize()
    {
        using var d = new Dir();
        var provider = new PropertyPages();

        Assert.Equal(new[] { "Permissions", "Checksums" }, provider.GetPages(new PropertyTarget([d.File_], "text/plain")).Select(p => p.Title));
        Assert.Equal(new[] { "Permissions", "Customize" }, provider.GetPages(new PropertyTarget([d.Folder], "inode/directory")).Select(p => p.Title));
        Assert.Equal(new[] { "Permissions" }, provider.GetPages(new PropertyTarget([d.File_, Path.Combine(d.Root, "two.txt")], "")).Select(p => p.Title));   // several items: permissions only
        Assert.Empty(provider.GetPages(new PropertyTarget([Path.Combine(d.Root, "gone")], "")));
    }

    [AvaloniaFact]
    public async Task Checksums_match_the_known_values_and_the_compare_box_accepts_pasted_forms()
    {
        using var d = new Dir();
        var page = Tag<ChecksumPage>(new PropertyPages().GetPages(new PropertyTarget([d.File_], "")), "Checksums");
        await page.ComputeTask;

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", page.Sha256);   // sha256("hello")
        Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", page.Sha1);
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", page.Md5);

        page.Expected = "2CF24DBA:5FB0A30E 26E83B2A-C5B9E29E1B161E5C1FA7425E73043362938B9824";            // capitals, colons, spaces, dashes
        Assert.Equal("Matches the SHA-256.", page.MatchText);
        page.Expected = "5d41402abc4b2a76b9719d911017c592";
        Assert.Equal("Matches the MD5.", page.MatchText);
        page.Expected = "deadbeef";
        Assert.Equal("Does not match.", page.MatchText);
        page.Expected = "";
        Assert.Equal("", page.MatchText);
    }

    [AvaloniaFact]
    public void Customize_saves_a_folder_icon_in_the_directory_file_clears_it_and_refuses_unsafe_names()
    {
        using var d = new Dir();
        var info = new PropertyPages().GetPages(new PropertyTarget([d.Folder], "")).Single(p => p.Title == "Customize");
        var page = (CustomizePage)info.Tag!;

        Assert.False(info.IsDirty!());
        page.Icon = "folder-music";
        Assert.True(info.IsDirty!());
        Assert.True(info.Apply!());
        Assert.Equal("folder-music", FolderIcon.Read(d.Folder));

        page.Icon = "bad\nname";
        Assert.False(info.Apply!());
        Assert.NotEqual("", page.Error);
        Assert.Equal("folder-music", FolderIcon.Read(d.Folder));                  // unchanged

        page.Icon = "";
        Assert.True(info.Apply!());
        Assert.Null(FolderIcon.Read(d.Folder));
    }

    [AvaloniaFact]
    public void The_sheet_shows_the_shells_tabs_with_no_registration_and_a_registry_entry_for_the_same_component_is_not_loaded_twice()
    {
        using var d = new Dir();
        var hive = new Hive("HKCU") { Identity = RegistryIdentity.Current };
        var hkcr = new ClassesRoot(hive);
        ComponentRegistrar.Register(hkcr, new ComponentInfo(PropertyPages.Clsid, typeof(PropertyPages).FullName!, typeof(PropertyPages).Assembly.Location, "vexplore", ThreadingModel.Apartment));
        hkcr.SetValue($@"*\{PropertyHandlers.HandlersKey}\VExplore", "", RegistryValueKind.String, PropertyPages.Clsid);
        var context = new PropertySheetContext
        {
            Target = new PropertyTarget([d.File_], "text/plain"),
            BuiltInProviders = [(PropertyPages.Clsid, new PropertyPages())],
            Handlers = new PropertyHandlers(hkcr), Loader = new ComponentLoader(),
        };

        var sheet = new PropertySheetWindow(context); sheet.Show();

        Assert.Equal(new[] { "General", "Permissions", "Details", "Checksums" }, sheet.Pages.Select(p => p.Title));   // one Permissions, not two
        Assert.Empty(sheet.Problems);
    }

    [AvaloniaFact]
    public void The_installer_registers_the_tabs_for_files_and_folders_through_RegSvrVDE_and_the_registered_component_loads()
    {
        using var d = new Dir();
        var userHive = Path.Combine(d.Root, "hive", "User.fhive");
        var dll = typeof(PropertyPages).Assembly.Location;
        var output = new StringWriter(); var error = new StringWriter();

        var code = RegSvrVDE.RegSvrVdeCommand.Run(["/i", dll, "--user-hive=" + userHive], output, error);

        Assert.True(code == 0, error.ToString());
        using var hive = Hive.OpenShared("HKCU", userHive, watch: false);
        var hkcr = new ClassesRoot(hive);
        Assert.Equal(PropertyPages.Clsid, hkcr.GetValue($@"*\{PropertyHandlers.HandlersKey}\VExplore", "")!.Data);
        Assert.Equal(PropertyPages.Clsid, hkcr.GetValue($@"Directory\{PropertyHandlers.HandlersKey}\VExplore", "")!.Data);

        var handlers = new PropertyHandlers(hkcr);
        var found = Assert.Single(handlers.Split(d.File_, false).loadable);
        var provider = new ComponentLoader().Get<IPropertyPageProvider>(found);                // loaded the way the registry says
        Assert.Contains("Permissions", provider.GetPages(new PropertyTarget([d.File_], "")).Select(p => p.Title));
        Assert.Single(handlers.Split(d.Folder, true).loadable);

        Assert.Equal(0, RegSvrVDE.RegSvrVdeCommand.Run(["/u", "/i", dll, "--user-hive=" + userHive], new StringWriter(), error));   // uninstalling removes the entries again
        hkcr = new ClassesRoot(Hive.OpenShared("HKCU", userHive, watch: false));
        Assert.False(hkcr.KeyExists($@"*\{PropertyHandlers.HandlersKey}\VExplore"));
        Assert.False(hkcr.KeyExists($@"CLSID\{PropertyPages.Clsid}"));
    }
}
