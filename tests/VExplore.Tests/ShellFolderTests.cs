using Avalonia.Headless.XUnit;
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
        Assert.Equal(new[] { "Documents", "This Computer", "Network", "Trash" }, catalog.Namespace("Sidebar").Select(i => i.Name));   // Control Panel is not in the sidebar
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
        await folder.InvokeAsync("restore", item, default);
        Assert.True(File.Exists(file));
        Assert.Empty(folder.GetItems());
        Assert.Equal(1, changed);
        await Assert.ThrowsAsync<ArgumentException>(() => folder.InvokeAsync("explode", null, default));
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

        await folder.InvokeAsync("open", items[0], default);
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
        Assert.Equal(new[] { "Documents", "This Computer", "Network", "Trash" }, catalog.Namespace("Sidebar").Select(i => i.Name));
        Assert.Equal("Trash", catalog.Open(trash).DisplayName);   // loaded through the apartment-aware loader
    }

    [AvaloniaFact]
    public void The_file_manager_opens_a_shell_folder_by_address_and_its_sidebar_places_lead_there()
    {
        using var e = new Env();
        var catalog = Catalog(e, new Hive("HKCU"), new Hive("HKLM"));
        var window = new FileManagerWindow(Path.Combine(e.Root, "home"), e.Folders, _ => { }, shellFolders: catalog);
        window.Show();

        window.Navigate("shell:Documents");
        Assert.True(window.InShell);
        Assert.Equal(new[] { "sub", "a.txt" }, window.Entries.Select(x => x.Name));

        window.Navigate("shell:Computer");
        Assert.Contains(window.Entries, x => x.Name == "Documents");

        window.Navigate(Path.Combine(e.Root, "home"));
        Assert.False(window.InShell);
    }
}
