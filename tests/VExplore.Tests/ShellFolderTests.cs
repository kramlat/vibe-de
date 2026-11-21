using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vde.Components;
using Vde.Freedesktop;
using Vde.Shell;
using Vibe.Registry;
using VExplore.Windows;

namespace VExplore.Tests;

public class ShellFolderTests
{
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vexplore-sf-" + Guid.NewGuid().ToString("N"));
        public XdgDirectories Xdg { get; }
        public KnownFolders Folders { get; }
        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Root, "home", "Documents", "sub"));
            File.WriteAllText(Path.Combine(Root, "home", "Documents", "a.txt"), "hi");
            File.WriteAllText(Path.Combine(Root, "home", "Documents", ".hidden"), "x");
            var env = new Dictionary<string, string> { ["HOME"] = Path.Combine(Root, "home"), ["XDG_CONFIG_HOME"] = Path.Combine(Root, "cfg"), ["XDG_DATA_HOME"] = Path.Combine(Root, "data") };
            Xdg = new XdgDirectories(k => env.GetValueOrDefault(k));
            Folders = new KnownFolders(Xdg);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static ShellFolderCatalog Catalog(Env e, Hive user, Hive system, bool builtIns = true)
    {
        var catalog = new ShellFolderCatalog(new ClassesRoot(user, system), system, user, new ComponentLoader());
        if (builtIns) ShellFolderSetup.AddBuiltIns(catalog, e.Folders);
        return catalog;
    }

    [Fact]
    public void The_five_shell_folders_resolve_by_name_and_the_sidebar_lists_them_in_order()
    {
        using var e = new Env();
        var catalog = Catalog(e, new Hive("HKCU"), new Hive("HKLM"));

        foreach (var name in new[] { "Computer", "Network", "Trash", "Documents", "ControlPanel" })
            Assert.NotNull(catalog.Resolve("shell:" + name));
        Assert.Equal(ShellFolderIds.Trash, catalog.Resolve("::" + ShellFolderIds.Trash)!.Clsid, ignoreCase: true);
        Assert.Equal(new[] { "Quick access", "Home", "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "This Computer", "Network", "Trash" }, catalog.Namespace("Sidebar").Select(i => i.Name));   // Control Panel is not in the sidebar
        Assert.Null(catalog.Resolve("shell:Nowhere"));
    }

    [Fact]
    public void The_user_can_hide_a_folder_from_the_sidebar()
    {
        using var e = new Env();
        var user = new Hive("HKCU");
        user.SetValue($@"{ShellFolderCatalog.NamespacesKey}\Sidebar\{ShellFolderIds.Network}", "Hidden", RegistryValueKind.DWord, 1u);
        var catalog = Catalog(e, user, new Hive("HKLM"));

        Assert.DoesNotContain(catalog.Namespace("Sidebar"), i => i.Name == "Network");
        Assert.NotNull(catalog.Resolve("shell:Network"));   // still reachable by name
    }

    [Fact]
    public void Documents_lists_the_real_files_without_dotfiles_and_folders_first()
    {
        using var e = new Env();
        var items = new DocumentsFolder(e.Folders).GetItems();

        Assert.Equal(new[] { "sub", "a.txt" }, items.Select(i => i.Name));
        Assert.True(items[0].IsFolder);
        Assert.Equal(Path.Combine(e.Root, "home", "Documents", "a.txt"), items[1].FileSystemPath);
    }

    [Fact]
    public async Task Trash_offers_restore_and_delete_per_item_and_empty_for_the_folder()
    {
        using var e = new Env();
        var trash = new Trash(e.Xdg);
        var file = Path.Combine(e.Root, "home", "Documents", "a.txt");
        trash.Send(file);
        var folder = new TrashFolder(trash);

        var item = Assert.Single(folder.GetItems());
        Assert.Equal(new[] { "restore", "delete" }, folder.GetActions(item).Select(a => a.Id));
        Assert.Equal(new[] { "empty" }, folder.GetActions(null).Select(a => a.Id));

        var changed = 0;
        folder.Changed += (_, _) => changed++;
        await folder.InvokeAsync("restore", item, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(file));
        Assert.Empty(folder.GetItems());
        Assert.Equal(1, changed);
        await Assert.ThrowsAsync<ArgumentException>(() => folder.InvokeAsync("explode", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Control_Panel_lists_the_pages_and_opening_one_runs_it()
    {
        var catalog = ControlPanelCatalog.Load(null, null, null, []);
        ControlPanelItem? opened = null;
        var folder = new ControlPanelFolder(() => catalog, i => opened = i);

        var items = folder.GetItems();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal("open", i.DefaultAction));

        await folder.InvokeAsync("open", items[0], TestContext.Current.CancellationToken);
        Assert.Equal("cp:" + opened!.Id, items[0].Id);
    }

    [Fact]
    public void RegSvrVDE_registers_the_folders_and_a_catalog_without_built_ins_finds_and_loads_them()
    {
        using var e = new Env();
        var userHive = Path.Combine(e.Root, "hive", "User.fhive");
        var output = new StringWriter(); var error = new StringWriter();
        var code = RegSvrVDE.RegSvrVdeCommand.Run(["/i", typeof(ShellFolderIds).Assembly.Location, "--user-hive=" + userHive], output, error);
        Assert.True(code == 0, error.ToString());

        using var hive = Hive.OpenShared("HKCU", userHive, watch: false);
        var catalog = new ShellFolderCatalog(new ClassesRoot(hive), null, hive, new ComponentLoader());   // no built-ins: everything comes from the registry
        var trash = catalog.Resolve("shell:Trash");
        Assert.NotNull(trash);
        Assert.False(trash!.IsBuiltIn);
        Assert.Contains("Sidebar", trash.Namespaces);
        Assert.Equal(new[] { "Quick access", "Home", "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "This Computer", "Network", "Trash" }, catalog.Namespace("Sidebar").Select(i => i.Name));
        Assert.Equal("Trash", catalog.Open(trash).DisplayName);   // loaded through the apartment-aware loader
        foreach (var (clsid, parsing, name, _) in ShellFolderIds.All)   // every shell folder is in the registry, whole
        {
            var info = catalog.Resolve("shell:" + parsing);
            Assert.NotNull(info);
            Assert.False(info!.IsBuiltIn);
            Assert.Equal(name, info.Name);
            Assert.Equal(ShellFolderIds.Places(clsid).Order(), info.Namespaces.Order());
            Assert.Equal(name, catalog.Open(info).DisplayName);
        }
        Assert.Equal(new[] { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos" }, catalog.Namespace("QuickAccess").Select(i => i.Name));   // the user's places are the Quick access namespace
        Assert.Equal(new[] { "This Computer", "Network", "Trash" }, catalog.Namespace("Desktop").Select(i => i.Name));   // registered for the desktop: their icons
    }

    [AvaloniaFact]
    public void The_file_manager_opens_a_shell_folder_by_address_and_its_sidebar_places_lead_there()
    {
        using var e = new Env();
        var catalog = Catalog(e, new Hive("HKCU"), new Hive("HKLM"));
        var window = new FileManagerWindow(Path.Combine(e.Root, "home"), e.Folders, _ => { }, shellFolders: catalog);
        window.Show();

        window.Navigate("shell:Documents");   // a folder standing for a directory opens the directory itself
        Assert.False(window.InShell);
        Assert.Equal(Path.Combine(Path.Combine(e.Root, "home"), "Documents"), window.CurrentFolder);
        Assert.Equal(new[] { "sub", "a.txt" }, window.Entries.Select(x => x.Name));

        window.Navigate("shell:QuickAccess");
        Assert.Contains(window.Entries, x => x.Name == "Documents" && x.Group == ComputerGroups.Folders);   // the user's places are in Quick access
        Assert.DoesNotContain(window.Entries, x => x.Name == "Documents" && x.Group != ComputerGroups.Folders);
        var documents = window.Entries.Single(x => x.Name == "Documents");
        Assert.Equal(Path.Combine(e.Root, "home"), documents.Detail);                                          // under the name: where it is

        window.Navigate("shell:Computer");
        Assert.DoesNotContain(window.Entries, x => x.Name == "Documents");                                      // not in This Computer any more

        window.Navigate(Path.Combine(e.Root, "home"));
        Assert.False(window.InShell);
    }

    [AvaloniaFact]
    public void This_computer_starts_as_tiles_grouped_by_type_with_the_folders_first_and_other_folders_are_medium_icons()
    {
        using var e = new Env();
        var catalog = Catalog(e, new Hive("HKCU"), new Hive("HKLM"));
        var home = Path.Combine(e.Root, "home");
        var window = new FileManagerWindow(home, e.Folders, _ => { }, shellFolders: catalog);
        window.Show();
        Assert.Equal(Vde.Shell.ViewMode.MediumIcons, window.ActiveView.ViewMode);

        window.Navigate("shell:QuickAccess");
        Assert.Equal(Vde.Shell.ViewMode.Tiles, window.ActiveView.ViewMode);
        Assert.Equal(Vde.Shell.GroupBy.Type, window.ActiveView.GroupBy);
        Assert.Equal(Vde.Shell.ComputerGroups.Folders, window.ActiveView.GroupTitles[0]);
        Assert.Equal(Vde.Shell.ComputerGroups.Folders, window.ActiveView.Entries[0].Group);
        window.Navigate("shell:Computer");
        Assert.Equal(Vde.Shell.ViewMode.Tiles, window.ActiveView.ViewMode);

        window.ActiveView.ViewMode = Vde.Shell.ViewMode.LargeIcons;   // the user's choice is kept for this place
        window.Navigate(home);
        Assert.Equal(Vde.Shell.ViewMode.MediumIcons, window.ActiveView.ViewMode);
        window.Navigate("shell:Computer");
        Assert.Equal(Vde.Shell.ViewMode.LargeIcons, window.ActiveView.ViewMode);
    }
}

public class ExplorerCommandBarTests
{
    [AvaloniaFact]
    public void The_command_bar_ends_with_the_overflow_menu_of_selection_commands_properties_and_options()
    {
        var root = Path.Combine(Path.GetTempPath(), "vexplore-ov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, n), "x");
        try
        {
            var opened = new List<string>();
            var env = new Dictionary<string, string> { ["HOME"] = root, ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg") };
            var folders = new KnownFolders(new Vde.Freedesktop.XdgDirectories(k => env.GetValueOrDefault(k)));
            var view = new FileManagerView(root, folders, _ => { }, openSettingsPage: opened.Add);
            var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view };
            window.Show();

            Assert.Contains(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>(), b => Equals(b.Tag, "CommandOverflow"));
            Assert.Equal(new[] { "Select all", "Select none", "Invert selection", "Properties", "Options" }, view.OverflowMenu().Where(m => m is not null).Select(m => m!.Text));
            Assert.False(view.OverflowMenu().First(m => m?.Text == "Properties")!.Enabled);   // grey with nothing selected

            view.OverflowMenu().First(m => m?.Text == "Select all")!.Click!();
            Assert.Equal(3, view.SelectedPaths.Count);
            Assert.True(view.OverflowMenu().First(m => m?.Text == "Properties")!.Enabled);
            view.OverflowMenu().First(m => m?.Text == "Select none")!.Click!();
            Assert.Empty(view.SelectedPaths);
            view.SelectEntries("a.txt");
            view.OverflowMenu().First(m => m?.Text == "Invert selection")!.Click!();
            Assert.Equal(new[] { "b.txt", "c.txt" }, view.SelectedPaths.Select(Path.GetFileName).Order());

            view.OverflowMenu().First(m => m?.Text == "Options")!.Click!();
            Assert.Equal(new[] { "fileexplorer" }, opened);                                      // the options page
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void File_Explorer_opens_to_the_place_the_options_name()
    {
        var hive = new Hive("HKCU");
        Assert.Equal("Home", Vde.Shell.ExplorerOptions.OpenTo(hive));
        hive.SetValue(Vde.Shell.ExplorerOptions.KeyPath, "OpenTo", RegistryValueKind.String, "QuickAccess");
        Assert.Equal("QuickAccess", Vde.Shell.ExplorerOptions.OpenTo(hive));
        hive.SetValue(Vde.Shell.ExplorerOptions.KeyPath, "OpenTo", RegistryValueKind.String, "garbage");
        Assert.Equal("Home", Vde.Shell.ExplorerOptions.OpenTo(hive));
    }
}
