using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell;
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

        Assert.Equal("Appearance", Assert.Single(window.ShownItems).Name);          // the first category is selected
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
