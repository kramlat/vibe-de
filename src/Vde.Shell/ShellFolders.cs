using Vde.Components;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record ShellFolderInfo(string Clsid, string ParsingName, string Name, ShellFolderAttributes Attributes, bool IsBuiltIn, ComponentRegistration? Registration)
{
    // Where it appears ("Sidebar", "Desktop") and in what order (lower first), from CLSID\{guid}\ShellFolder.
    public IReadOnlyList<string> Namespaces { get; init; } = [];
    public int Order { get; init; } = 100;

    public string Path => "shell:" + ParsingName;
}

// Finds and opens shell folders: the ones built into the shell and the ones components registered with a
// CLSID\{guid}\ShellFolder key. Everything is looked up by parsing name ("shell:Computer", or "::{clsid}").
// Opening a registered folder loads its component, which only happens for trusted registrations, and on the
// calling thread (apartment components must stay on the UI thread).
public sealed class ShellFolderCatalog
{
    public const string NamespacesKey = @"Software\Vde\Explorer\Namespaces";

    private readonly ClassesRoot _classes;
    private readonly IHive? _system, _user;
    private readonly ComponentCatalog _components;
    private readonly ComponentLoader _loader;
    private readonly Dictionary<string, (ShellFolderInfo info, IShellFolder folder)> _builtIns = new(StringComparer.OrdinalIgnoreCase);

    public ShellFolderCatalog(ClassesRoot classes, IHive? system, IHive? user, ComponentLoader loader, RegistryIdentity? identity = null)
    {
        _classes = classes;
        _system = system;
        _user = user;
        _loader = loader;
        _components = new ComponentCatalog(classes, identity);
    }

    // A folder built into the shell: always there, needs no registration.
    public void AddBuiltIn(string clsid, string parsingName, string name, ShellFolderAttributes attributes, IShellFolder folder,
        IReadOnlyList<string>? namespaces = null, int order = 100)
        => _builtIns[ComponentInfo.NormalizeGuid(clsid) ?? throw new ArgumentException("Not a GUID.", nameof(clsid))]
            = (new ShellFolderInfo(ComponentInfo.NormalizeGuid(clsid)!, parsingName, name, attributes, true, null) { Namespaces = namespaces ?? [], Order = order }, folder);

    public IReadOnlyList<ShellFolderInfo> All()
    {
        var list = _builtIns.Values.Select(b => b.info).ToList();
        if (_classes.KeyExists("CLSID"))
            foreach (var clsid in _classes.GetSubKeyNames("CLSID"))
                if (!_builtIns.ContainsKey(clsid) && Read(clsid) is { } info)
                    list.Add(info);
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // "shell:Computer", "Computer" or "::{CLSID}".
    public ShellFolderInfo? Resolve(string path)
    {
        if (path.StartsWith("::", StringComparison.Ordinal))
            return ComponentInfo.NormalizeGuid(path[2..]) is { } id ? All().FirstOrDefault(i => i.Clsid == id) : null;
        var name = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? path[6..] : path;
        return All().FirstOrDefault(i => string.Equals(i.ParsingName, name, StringComparison.OrdinalIgnoreCase));
    }

    // The folders that appear in a place ("Sidebar", "Desktop"), in their Order. A folder lists the places it wants to
    // appear in; the user (or an administrator) hides one with  Software\Vde\Explorer\Namespaces\<place>\<clsid>  Hidden=1.
    public IReadOnlyList<ShellFolderInfo> Namespace(string root)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { _system, _user })
        {
            var key = $@"{NamespacesKey}\{root}";
            if (hive is null || !hive.KeyExists(key)) continue;
            foreach (var name in hive.GetSubKeyNames(key))
                if (ComponentInfo.NormalizeGuid(name) is { } clsid && hive.GetValue($@"{key}\{name}", "Hidden") is { Kind: RegistryValueKind.DWord, Data: uint h } && h != 0)
                    hidden.Add(clsid);
        }
        return All().Where(i => i.Namespaces.Contains(root, StringComparer.OrdinalIgnoreCase) && !hidden.Contains(i.Clsid) && !i.Attributes.HasFlag(ShellFolderAttributes.Hidden))
            .OrderBy(i => i.Order).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Throws ComponentLoadException when a registered folder is not trusted or fails to load.
    public IShellFolder Open(ShellFolderInfo info)
    {
        if (_builtIns.TryGetValue(info.Clsid, out var builtIn))
            return builtIn.folder;
        var registration = info.Registration ?? throw new ComponentLoadException($"{info.Name} has no component to load.");
        return _loader.Get<IShellFolder>(registration);
    }

    private ShellFolderInfo? Read(string clsid)
    {
        var key = $@"CLSID\{clsid}\ShellFolder";
        if (!_classes.KeyExists(key) || _classes.GetValue(key, "ParsingName")?.Data is not string { Length: > 0 } parsing)
            return null;
        var registration = _components.Find(clsid);
        if (registration is null)
            return null;   // a ShellFolder key with no server behind it: nothing to open
        var attributes = _classes.GetValue(key, "Attributes") is { Kind: RegistryValueKind.DWord, Data: uint a } ? (ShellFolderAttributes)a : ShellFolderAttributes.Folder;
        var name = _classes.GetValue(key, "Name")?.Data as string ?? registration.Component.Description ?? parsing;
        var places = _classes.GetValue(key, "Namespaces") is { Kind: RegistryValueKind.MultiString } n ? (string[])n.Data! : [];
        var order = _classes.GetValue(key, "Order") is { Kind: RegistryValueKind.DWord, Data: uint o } ? (int)Math.Min(o, int.MaxValue) : 100;
        return new ShellFolderInfo(clsid, parsing, name, attributes, false, registration) { Namespaces = places, Order = order };
    }
}

// ---- the folders the shell itself provides ----

public abstract class ShellFolderBase : IShellFolder
{
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<string> IconNames { get; }
    public abstract IReadOnlyList<ShellItem> GetItems();
    public virtual IReadOnlyList<ShellAction> GetActions(ShellItem? item) => [];
    public virtual Task InvokeAsync(string actionId, ShellItem? item, CancellationToken cancel) => Task.CompletedTask;
    public event EventHandler? Changed;
    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

// "This Computer": the user's folders and the mounted drives.
public class ComputerFolder(KnownFolders? folders = null, Computer? computer = null) : ShellFolderBase
{
    private readonly KnownFolders _folders = folders ?? new KnownFolders();
    private readonly Computer _computer = computer ?? new Computer();

    public override string DisplayName => "This Computer";
    public override IReadOnlyList<string> IconNames => ["computer", "drive-harddisk"];

    public override IReadOnlyList<ShellItem> GetItems()
    {
        var items = new List<ShellItem>();
        foreach (var (folder, name, icon) in new[] { (KnownFolder.Desktop, "Desktop", "user-desktop"), (KnownFolder.Documents, "Documents", "folder-documents"), (KnownFolder.Downloads, "Downloads", "folder-download"),
                     (KnownFolder.Music, "Music", "folder-music"), (KnownFolder.Pictures, "Pictures", "folder-pictures"), (KnownFolder.Videos, "Videos", "folder-videos") })
            if (Directory.Exists(_folders.Get(folder)))
                items.Add(new ShellItem("folder:" + name, name, true, [icon, "folder"]) { TypeText = "Folder", FileSystemPath = _folders.Get(folder) });

        foreach (var v in _computer.GetVolumes())
            items.Add(new ShellItem("drive:" + v.Device + "|" + v.MountPoint, v.Label, true, v.Names())
            {
                TypeText = v.Kind switch { VolumeKind.Removable => "Removable disk", VolumeKind.Optical => "Optical drive", VolumeKind.Network => "Network drive", _ => "Local disk" },
                Detail = v.TotalBytes > 0 ? $"{ByteFormat.Format(v.FreeBytes)} free of {ByteFormat.Format(v.TotalBytes)}" : v.MountPoint,
                Size = v.TotalBytes > 0 ? v.TotalBytes : null,
                FileSystemPath = v.MountPoint,
            });
        return items;
    }
}

// "Network": the shares that are mounted.
public class NetworkFolder(Computer? computer = null) : ShellFolderBase
{
    private readonly Computer _computer = computer ?? new Computer();

    public override string DisplayName => "Network";
    public override IReadOnlyList<string> IconNames => ["network-workgroup", "folder-remote"];

    public override IReadOnlyList<ShellItem> GetItems()
        => _computer.GetNetworkShares().Select(s => new ShellItem("share:" + s.Url, s.Name, true, s.Names())
        {
            TypeText = "Network share", Detail = s.Url, FileSystemPath = s.MountPoint,
        }).ToList();
}

// "Trash": what was deleted, with Restore and Delete permanently per item and Empty Trash for all of it.
public class TrashFolder(Trash? trash = null) : ShellFolderBase
{
    private readonly Trash _trash = trash ?? new Trash();

    public override string DisplayName => "Trash";
    public override IReadOnlyList<string> IconNames => _trash.IsEmpty ? ["user-trash"] : ["user-trash-full", "user-trash"];

    public override IReadOnlyList<ShellItem> GetItems()
        => _trash.List().Select(t => new ShellItem("trash:" + t.StoredName, Path.GetFileName(t.OriginalPath.TrimEnd('/')), false, [t.IsDirectory ? "folder" : "text-x-generic"])
        {
            TypeText = t.IsDirectory ? "Folder" : "File",
            Detail = $"from {Path.GetDirectoryName(t.OriginalPath)} · deleted {t.DeletedLocal:g}",
            ModifiedUtc = t.DeletedLocal.ToUniversalTime(),
        }).ToList();

    public override IReadOnlyList<ShellAction> GetActions(ShellItem? item)
        => item is null
            ? (_trash.IsEmpty ? [] : [new ShellAction("empty", "Empty Trash", Dangerous: true)])
            : [new ShellAction("restore", "Restore"), new ShellAction("delete", "Delete permanently", Dangerous: true)];

    public override Task InvokeAsync(string actionId, ShellItem? item, CancellationToken cancel)
    {
        var entry = item is null ? null : _trash.List().FirstOrDefault(t => "trash:" + t.StoredName == item.Id);
        switch (actionId)
        {
            case "empty" when item is null: _trash.Empty(); break;
            case "restore" when entry is not null: _trash.Restore(entry); break;
            case "delete" when entry is not null: _trash.Delete(entry); break;
            default: throw new ArgumentException($"'{actionId}' is not an action of the Trash here.", nameof(actionId));
        }
        RaiseChanged();
        return Task.CompletedTask;
    }
}

// "Documents": the user's documents folder, presented as a shell folder (its items are the real files and folders).
public class DocumentsFolder(KnownFolders? folders = null) : ShellFolderBase
{
    private readonly string _path = (folders ?? new KnownFolders()).Get(KnownFolder.Documents);

    public override string DisplayName => "Documents";
    public override IReadOnlyList<string> IconNames => ["folder-documents", "folder"];

    public override IReadOnlyList<ShellItem> GetItems()
    {
        if (!Directory.Exists(_path)) return [];
        var items = new List<ShellItem>();
        foreach (var entry in new DirectoryInfo(_path).EnumerateFileSystemInfos().OrderBy(e => e is not DirectoryInfo).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (entry.Name.StartsWith('.')) continue;
            var dir = entry is DirectoryInfo;
            items.Add(new ShellItem("file:" + entry.Name, entry.Name, dir, [dir ? "folder" : "text-x-generic"])
            {
                TypeText = dir ? "Folder" : "File",
                Size = entry is FileInfo f ? f.Length : null,
                ModifiedUtc = entry.LastWriteTimeUtc,
                FileSystemPath = entry.FullName,
            });
        }
        return items;
    }
}

// "Control Panel": the settings pages and applets; opening one runs it.
public class ControlPanelFolder(Func<ControlPanelCatalog> catalog, Action<ControlPanelItem> open) : ShellFolderBase
{
    public override string DisplayName => "Control Panel";
    public override IReadOnlyList<string> IconNames => ["preferences-system", "applications-system"];

    public override IReadOnlyList<ShellItem> GetItems()
        => catalog().Items.OrderBy(i => Array.IndexOf(ControlPanelCategories.All, i.Category)).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new ShellItem("cp:" + i.Id, i.Name, false, [i.Icon, "preferences-system"])
            {
                TypeText = i.Category, Detail = i.Description, DefaultAction = "open",
            }).ToList();

    public override IReadOnlyList<ShellAction> GetActions(ShellItem? item) => item is null ? [] : [new ShellAction("open", "Open")];

    public override Task InvokeAsync(string actionId, ShellItem? item, CancellationToken cancel)
    {
        if (actionId != "open" || item is null) throw new ArgumentException($"'{actionId}' is not an action of the Control Panel.", nameof(actionId));
        var found = catalog().Items.FirstOrDefault(i => "cp:" + i.Id == item.Id) ?? throw new ArgumentException("No such item.", nameof(item));
        open(found);
        return Task.CompletedTask;
    }
}
