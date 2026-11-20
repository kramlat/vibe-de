using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.ComDlg;
using Vde.Shell.Controls;
using Vde.Shell.Theme;
using Vibe.Registry;

namespace VExplore.Windows;

// The registry editor: the hive roots on the left as a collapsible tree, the values of the chosen key on the
// right, and .reg import and export on the File menu. It edits the hives it was given (a RegistryStore's HKCU
// and HKLM, whether those are hive files here or a daemon's over RemoteHive) and follows their change events,
// so a value another process writes shows up at once. Denied work is told the way the registry tells
// everything: UnauthorizedAccessException, which lands here as a message box.
public sealed class RegEditWindow : Window
{
    private readonly KnownFolders _folders;
    private readonly IHive _user, _machine;
    private readonly List<RegKeyNode> _roots = [];
    private readonly ObservableCollection<RegKeyNode> _visible = [];
    private readonly ListBox _tree = new();
    private readonly ListBox _values = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _address = new();
    private readonly Border _statusHost = new();
    private MenuItem? _favoritesMenu;
    private static readonly string FavoritesPath = @"Software\Vde\Regedit\Favorites";
    private RegKeyNode? _selected;
    private bool _refreshing;

    public RegEditWindow(IHive currentUser, IHive localMachine, KnownFolders? folders = null)
    {
        _user = currentUser;
        _machine = localMachine;
        _folders = folders ?? new KnownFolders();
        foreach (var (name, hive) in new[] { (RegEditModel.UserRoot, _user), (RegEditModel.MachineRoot, _machine) })
        {
            var root = new RegKeyNode(name, "", name, hive, 0);
            root.Inspect();
            _roots.Add(root);
        }

        Title = "Registry Editor";
        Width = 880; Height = 560;
        MinWidth = 560; MinHeight = 380;

        _tree.ItemsSource = _visible;
        _tree.ItemTemplate = new FuncDataTemplate<RegKeyNode>((node, _) => node is null ? null : KeyRow(node));
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is RegKeyNode { } node && !ReferenceEquals(_selected, node)) Select(node);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (_selected is not { } node) return;
            switch (e.Key)
            {
                case Key.Right: Expand(node); e.Handled = true; break;
                case Key.Left when node.Depth > 0:
                    if (node.Expanded) Collapse(node);
                    else SelectPath(RegEditModel.ParentOf(node.FullPath));
                    e.Handled = true;
                    break;
            }
        };

        _values.ItemTemplate = new FuncDataTemplate<RegValueRow>((row, _) => row is null ? null : ValueRow(row));
        _values.DoubleTapped += (_, _) => ModifyValue();
        _values.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter: ModifyValue(); e.Handled = true; break;
                case Key.Delete: Run(DeleteValue); e.Handled = true; break;
            }
        };

        foreach (var hive in new[] { _user, _machine })
            hive.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

        _tree.ContextMenu = KeyMenu();
        _values.ContextMenu = ValueMenu();

        Content = BuildLayout();

        RebuildVisible();
        _tree.SelectedIndex = 0;   // HKEY_CURRENT_USER: its values (usually none) show at once
    }

    // ---- what tests and the menus reach ----

    // The address line as regedit shows it: the selected key in front of its imaginary Computer\ share.
    public string? AddressText => _address.Text;
    public IReadOnlyList<string> VisiblePaths => _visible.Select(n => n.FullPath).ToList();
    public string? SelectedPath => _selected?.FullPath;
    public IReadOnlyList<string> ShownValues => _values.Items.OfType<RegValueRow>().Select(r => r.Name).ToList();
    public IReadOnlyList<RegValueRow> ShownValueRows => _values.Items.OfType<RegValueRow>().ToList();

    // Selects by full display path ("HKEY_CURRENT_USER\Software"), expanding what is needed on the way.
    public bool SelectPath(string fullPath)
    {
        var parts = fullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var node = _roots.FirstOrDefault(r => r.Display.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        for (int i = 1; i < parts.Length && node is not null; i++)
        {
            Expand(node);
            node = node.Children.FirstOrDefault(c => c.Display.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
        }
        if (node is null) return false;
        Select(node);
        return true;
    }

    // A new key below the named one, by default named the way regedit names keys, and selected afterwards.
    // parentPath must be a path the window has open (see SelectPath). Returns the new key's full path.
    public string CreateKey(string parentPath, string? name = null)
    {
        var parent = Walk(parentPath);
        if (parent is null) throw new KeyNotFoundException($"Key '{parentPath}' is not open in the tree.");
        name ??= NextName(parent.Children.Select(c => c.Display), "New key");
        var relative = parent.Path.Length == 0 ? name : parent.Path + "\\" + name;
        if (parent.Hive.KeyExists(relative))
            throw new ArgumentException($"The key '{relative}' already exists.");
        parent.Hive.CreateKey(relative);
        Expand(parent);
        Refresh();
        SelectPath(parent.FullPath + "\\" + name);
        return parent.FullPath + "\\" + name;
    }

    public void DeleteSelectedKey()
    {
        var node = _selected ?? throw new InvalidOperationException("No key is selected.");
        if (node.Depth == 0) throw new ArgumentException("The root key cannot be deleted.");
        node.Hive.DeleteKey(node.Path);   // goes with everything below it, as DeleteKey does
        Refresh();
        SelectPath(RegEditModel.ParentOf(node.FullPath));
    }

    public void RenameSelectedKey(string name)
    {
        var node = _selected ?? throw new InvalidOperationException("No key is selected.");
        RegEditModel.Rename(node.Hive, node.Path, name);
        Refresh();
        SelectPath(RegEditModel.ParentOf(node.FullPath) + "\\" + name);
    }

    // Writes a value from edited text. Throws ArgumentException when the text will not parse.
    public void SetValueText(RegistryValueKind kind, string name, string text)
    {
        var key = _selected ?? throw new InvalidOperationException("No key is selected.");
        key.Hive.SetValue(key.Path, name, kind, RegEditModel.Parse(kind, text));
        ShowValues();
    }

    // A new value with starting data (as regedit does), which then opens in the edit dialog on its own.
    // interactive=false (tests) writes the value and leaves it selected, without any dialog.
    public void NewValue(RegistryValueKind kind, bool interactive = true)
    {
        var key = _selected;
        if (key is null) return;
        var name = NextName(key.Hive.GetValues(key.Path).Select(v => v.Name), "New value");
        key.Hive.SetValue(key.Path, name, kind, RegEditModel.DefaultData(kind));
        ShowValues();
        var rows = _values.Items.OfType<RegValueRow>().ToList();
        var index = rows.FindIndex(r => r.Name == name);
        if (index >= 0) _values.SelectedIndex = index;
        if (interactive)
            _ = RunAsync(() => EditValueAsync(name, kind, RegEditModel.ParseableText(kind, RegEditModel.DefaultData(kind))));
    }

    // Deletes the value selected in the list.
    public void DeleteValue()
    {
        var key = _selected;
        var row = _values.SelectedItem as RegValueRow;
        if (key is null || row is null) throw new InvalidOperationException("No value is selected.");
        DeleteValueNamed(key, row.Name);
    }

    public void DeleteValue(string name)
    {
        var key = _selected ?? throw new InvalidOperationException("No key is selected.");
        DeleteValueNamed(key, name);
    }

    private void DeleteValueNamed(RegKeyNode key, string name)
    {
        if (!key.Hive.DeleteValue(key.Path, name)) throw new KeyNotFoundException($"No value '{name}'.");
        ShowValues();
    }

    public void RenameValue(string oldName, string newName)
    {
        var key = _selected ?? throw new InvalidOperationException("No key is selected.");
        if (oldName.Length == 0)
            throw new ArgumentException("The default value has no name to change.");
        if (RegEditModel.NameProblem(newName) is { } problem)
            throw new ArgumentException(problem);
        var value = key.Hive.GetValue(key.Path, oldName) ?? throw new KeyNotFoundException($"No value '{oldName}'.");
        if (!newName.Equals(oldName, StringComparison.OrdinalIgnoreCase) && key.Hive.GetValue(key.Path, newName) is not null)
            throw new ArgumentException($"A value named '{newName}' is already here.");
        key.Hive.SetValue(key.Path, newName, value.Kind, value.Data);
        if (key.Hive.GetValuePermissions(key.Path, oldName) is { } permissions)
            try { key.Hive.SetValuePermissions(key.Path, newName, permissions); }
            catch (UnauthorizedAccessException) { }
        key.Hive.DeleteValue(key.Path, oldName);
        ShowValues();
    }

    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            foreach (var root in _roots) RebuildChildren(root);
            if (_selected is { } selected && !selected.Hive.KeyExists(selected.Path))
                _selected = _roots.First(r => ReferenceEquals(r.Hive, selected.Hive));   // the key is gone: fall back to its hive root
            RebuildVisible();
            SyncSelection();
            ShowValues();
        }
        finally { _refreshing = false; }
    }

    // ---- tree ----

    private void Select(RegKeyNode node)
    {
        _selected = node;
        _address.Text = "Computer\\" + node.FullPath;   // the way regedit's address line names a key
        SyncSelection();
        ShowValues();
    }

    private void SyncSelection()
    {
        var target = _selected is null ? null : _visible.FirstOrDefault(v => SameKey(v, _selected));
        if (!ReferenceEquals(_tree.SelectedItem, target))
            _tree.SelectedItem = target;
    }

    private void Toggle(RegKeyNode node)
    {
        if (!node.Expanded) Expand(node);
        else Collapse(node);
        Select(node);
    }

    private void Expand(RegKeyNode node)
    {
        LoadChildren(node);
        if (!node.Expanded)
        {
            node.Expanded = true;
            RebuildVisible();
            SyncSelection();
        }
    }

    private void Collapse(RegKeyNode node)
    {
        if (node.Expanded)
        {
            node.Expanded = false;
            RebuildVisible();
            SyncSelection();
        }
    }

    private void LoadChildren(RegKeyNode node)
    {
        if (!node.Loaded) node.SetChildren(ChildrenOf(node), loaded: true);
    }

    private IReadOnlyList<RegKeyNode> ChildrenOf(RegKeyNode node)
    {
        var list = new List<RegKeyNode>();
        try
        {
            foreach (var name in node.Hive.GetSubKeyNames(node.Path))
                list.Add(Build(node, name));
        }
        catch (UnauthorizedAccessException) { }
        catch (KeyNotFoundException) { }   // the key was deleted while we were looking
        return list;
    }

    // Reads the children of every loaded node again, keeping what is still there (and its expansion state).
    private void RebuildChildren(RegKeyNode node)
    {
        if (!node.Loaded)
        {
            node.Inspect();
            return;
        }
        var kept = node.Children.ToDictionary(c => c.Display, StringComparer.OrdinalIgnoreCase);
        var fresh = new List<RegKeyNode>();
        try
        {
            foreach (var name in node.Hive.GetSubKeyNames(node.Path))
            {
                if (kept.TryGetValue(name, out var old)) { RebuildChildren(old); fresh.Add(old); }
                else fresh.Add(Build(node, name));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (KeyNotFoundException) { }
        node.SetChildren(fresh, loaded: true);
    }

    private RegKeyNode Build(RegKeyNode node, string name)
    {
        var child = new RegKeyNode(name, node.Path.Length == 0 ? name : node.Path + "\\" + name, node.FullPath + "\\" + name, node.Hive, node.Depth + 1);
        child.Inspect();
        return child;
    }

    private void RebuildVisible()
    {
        _visible.Clear();
        void Emit(RegKeyNode node)
        {
            _visible.Add(node);
            if (node.Expanded)
                foreach (var child in node.Children)
                    Emit(child);
        }
        foreach (var root in _roots) Emit(root);
    }

    private RegKeyNode? Walk(string fullPath)
        => _visible.FirstOrDefault(v => v.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

    private static bool SameKey(RegKeyNode a, RegKeyNode b)
        => ReferenceEquals(a.Hive, b.Hive) && a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase);

    // ---- values ----

    private void ShowValues()
    {
        _values.ItemsSource = Rows();
        Status();
    }

    private IReadOnlyList<RegValueRow> Rows()
    {
        var key = _selected;
        if (key is null) return [];
        var rows = new List<RegValueRow>();
        try
        {
            var values = key.Hive.GetValues(key.Path)
                .OrderBy(v => v.Name.Length == 0 ? 0 : 1).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
                rows.Add(new RegValueRow(value.Name, value.Kind, RegEditModel.KindName(value.Kind), RegEditModel.Format(value),
                    key.Hive.GetValuePermissions(key.Path, value.Name)));
        }
        catch (UnauthorizedAccessException) { }
        catch (KeyNotFoundException) { }
        return rows;
    }

    private void Status()
    {
        var key = _selected;
        if (key is null) { _status.Text = ""; return; }
        try
        {
            var info = key.Hive.GetKeyInfo(key.Path);
            _status.Text = $"{key.FullPath}    {info.SubKeyCount} keys, {info.ValueCount} values    {info.Permissions.Symbolic} ({info.Permissions.ModeText})";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or KeyNotFoundException)
        {
            _status.Text = key.FullPath;
        }
    }

    private void ModifyValue()
    {
        if (_values.SelectedItem is not RegValueRow { } row) return;
        if (row.Kind == RegistryValueKind.None)
        {
            _ = CommonDialogs.MessageBoxAsync(this, "A REG_NONE value has no data to change.", "Registry Editor");
            return;
        }
        var key = _selected!;
        var data = key.Hive.GetValue(key.Path, row.Name)?.Data;
        _ = RunAsync(() => EditValueAsync(row.Name, row.Kind, RegEditModel.ParseableText(row.Kind, data)));
    }

    private async Task EditValueAsync(string name, RegistryValueKind kind, string initial)
    {
        var key = _selected;
        if (key is null) return;
        var dialog = new ValueEditWindow(name, kind, initial, key.Hive.GetValuePermissions(key.Path, name));
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } text) return;   // cancelled
        try { SetValueText(kind, name, text); }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or KeyNotFoundException)
        {
            await CommonDialogs.MessageBoxAsync(this, e.Message, "Registry Editor", MessageButtons.Ok, MessageIcon.Error);
        }
    }

    // ---- menus ----

    // Menu commands show what went wrong instead of losing it: a denied write, a key that vanished, no selection.
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            _ = CommonDialogs.MessageBoxAsync(this, e.Message, "Registry Editor", MessageButtons.Ok, MessageIcon.Error);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            await CommonDialogs.MessageBoxAsync(this, e.Message, "Registry Editor", MessageButtons.Ok, MessageIcon.Error);
        }
    }

    private MenuItem Item(string text, Action click, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = text };
        if (gesture is not null) item.InputGesture = gesture;
        item.Click += (_, _) => Run(click);
        return item;
    }

    private Menu BuildMenu()
    {
        _favoritesMenu = new MenuItem { Header = "_Favorites" };
        RebuildFavorites();
        return new Menu { Items = { FileMenu(), EditMenu(), ViewMenu(), _favoritesMenu, HelpMenu() } };
    }

    private MenuItem FileMenu() => new()
    {
        Header = "_File",
        Items =
        {
            Item("Import .reg…", () => _ = ImportFileAsync()),
            Item("Export key as .reg…", () => _ = ExportKeyAsync()),
            new Separator(),
            Item("Exit", () => Close()),
        },
    };

    private MenuItem EditMenu()
    {
        var kinds = new MenuItem { Header = "New value" };
        foreach (var kind in RegEditModel.Kinds)
        {
            var k = kind;
            var item = new MenuItem { Header = RegEditModel.KindName(k) };
            item.Click += (_, _) => Run(() => NewValue(k));
            kinds.Items.Add(item);
        }
        return new MenuItem
        {
            Header = "_Edit",
            Items =
            {
                Item("New key", () => _ = NewKeyAsync()),
                kinds,
                Item("Modify", () => ModifyValue()),
                new Separator(),
                Item("Rename key…", () => _ = RenameKeyAsync()),
                Item("Delete key…", () => _ = DeleteKeyAsync()),
                Item("Delete value", () => DeleteValue()),
            },
        };
    }

    private MenuItem ViewMenu()
    {
        var toggle = new MenuItem { Header = "Status bar", IsChecked = true };
        toggle.Click += (_, _) => { if (_statusHost is { } bar) bar.IsVisible = !bar.IsVisible; };
        return new MenuItem
        {
            Header = "_View",
            Items =
            {
                Item("Refresh", () => Refresh(), new KeyGesture(Key.F5)),
                new Separator(),
                toggle,
            },
        };
    }

    // The bookmarks live as values under Software\Vde\Regedit\Favorites in the user hive: the name is the
    // wording of the menu item, its data the key path to jump to.
    private void RebuildFavorites()
    {
        if (_favoritesMenu is null) return;
        var add = new MenuItem { Header = "Add to Favorites…" };
        add.Click += (_, _) => Run(() => _ = AddFavoriteAsync());
        _favoritesMenu.Items.Clear();
        _favoritesMenu.Items.Add(add);
        try
        {
            if (_user.KeyExists(FavoritesPath))
                foreach (var favorite in _user.GetValues(FavoritesPath).Where(v => v.Kind == RegistryValueKind.String))
                {
                    var item = new MenuItem { Header = favorite.Name };
                    var path = (string?)favorite.Data;
                    item.Click += (_, _) => SelectPath(path ?? "");   // a path the tree has dropped just does nothing
                    _favoritesMenu.Items.Add(item);
                }
        }
        catch (UnauthorizedAccessException) { }
    }

    private async Task AddFavoriteAsync()
    {
        if (_selected is not { } node) return;
        var name = await AskAsync("Bookmark name:", node.Display, RegEditModel.NameProblem);
        if (name is not null) Run(() => AddFavorite(name));
    }

    // Adds the selected key to the Favorites menu (the menu itself asks for the bookmark name first).
    public void AddFavorite(string name)
    {
        if (_selected is not { } node)
            throw new InvalidOperationException("No key is selected.");
        if (RegEditModel.NameProblem(name) is { } problem)
            throw new ArgumentException(problem);
        _user.SetValue(FavoritesPath, name, RegistryValueKind.String, node.FullPath);
        RebuildFavorites();
    }

    private MenuItem HelpMenu() => new()
    {
        Header = "_Help",
        Items =
        {
            Item("About Registry Editor", () => _ = CommonDialogs.MessageBoxAsync(this,
                "Vibe Registry Editor\n\nThe keys and values of the user and the system hive.\n\nPart of the Vibe desktop.",
                "Registry Editor", MessageButtons.Ok, MessageIcon.Information)),
        },
    };

    private ContextMenu KeyMenu() => new()
    {
        Items =
        {
            Item("New key", () => _ = NewKeyAsync()),
            new Separator(),
            Item("Rename…", () => _ = RenameKeyAsync()),
            Item("Delete…", () => _ = DeleteKeyAsync()),
            new Separator(),
            Item("Export as .reg…", () => _ = ExportKeyAsync()),
        },
    };

    private ContextMenu ValueMenu() => new()
    {
        Items =
        {
            Item("Modify", () => ModifyValue()),
            Item("Rename…", () => _ = RenameValueAsync()),
            new Separator(),
            Item("Delete", () => Run(DeleteValue)),
        },
    };

    private async Task<string?> AskAsync(string title, string initial, Func<string, string?> validate)
        => await CommonDialogs.InputAsync(this, title, "Registry Editor", initial, validate);

    private async Task NewKeyAsync()
    {
        var name = await AskAsync("Key name:", NextName(_selected?.Children.Select(c => c.Display) ?? [], "New key"),
            n => RegEditModel.NameProblem(n));
        if (name is not null && _selected is { }) await RunAsync(() => { _ = CreateKey(SelectedPath!, name); return Task.CompletedTask; });
    }

    private async Task RenameKeyAsync()
    {
        if (_selected is not { Depth: > 0 } node) return;
        var name = await AskAsync("Key name:", node.Display, RegEditModel.NameProblem);
        if (name is not null && name != node.Display) await RunAsync(() => { RenameSelectedKey(name); return Task.CompletedTask; });
    }

    private async Task RenameValueAsync()
    {
        if (_values.SelectedItem is not RegValueRow { } row || row.Name.Length == 0) return;
        var name = await AskAsync("Value name:", row.Name, RegEditModel.NameProblem);
        if (name is not null && name != row.Name) await RunAsync(() => { RenameValue(row.Name, name); return Task.CompletedTask; });
    }

    private async Task DeleteKeyAsync()
    {
        if (_selected is not { Depth: > 0 } node) return;
        var yes = await CommonDialogs.MessageBoxAsync(this,
            node.HasChildren
                ? $"Delete '{node.Display}' and everything below it?"
                : $"Delete '{node.Display}'?",
            "Registry Editor", MessageButtons.YesNo, MessageIcon.Question, DialogResult.No);
        if (yes == DialogResult.Yes) await RunAsync(() => { DeleteSelectedKey(); return Task.CompletedTask; });
    }

    private async Task ImportFileAsync()
    {
        var picked = await CommonDialogs.OpenFileAsync(this, _folders, new FileDialogOptions
        {
            Title = "Import registry file",
            Filters = [new FileFilter("Registry files", ["*.reg"]), FileFilter.All],
        });
        if (picked is not { Count: 1 }) return;
        try
        {
            var applied = RegEditModel.Import(_user, _machine, RegFile.Load(picked[0]));
            await CommonDialogs.MessageBoxAsync(this, $"{applied} sections were imported.", "Registry Editor", MessageButtons.Ok, MessageIcon.Information);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException)
        {
            await CommonDialogs.MessageBoxAsync(this, e.Message, "Registry Editor", MessageButtons.Ok, MessageIcon.Error);
        }
    }

    private async Task ExportKeyAsync()
    {
        if (_selected is not { } node) return;
        var name = (node.Depth == 0 ? node.Display : node.Display) + ".reg";
        var path = await CommonDialogs.SaveFileAsync(this, _folders, new FileDialogOptions
        {
            Title = "Export registry key",
            FileName = name,
            DefaultExtension = "reg",
            Filters = [new FileFilter("Registry files", ["*.reg"]), FileFilter.All],
        });
        if (path is null) return;
        try
        {
            RegEditModel.Export(node.Hive, node.Path, path);
            Status();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await CommonDialogs.MessageBoxAsync(this, e.Message, "Registry Editor", MessageButtons.Ok, MessageIcon.Error);
        }
    }

    // ---- layout ----

    // The window the way regedit lays it out: the menu bar, the address line under it, the key tree beside
    // the values of the chosen key with Name/Type/Data headers, and the status bar along the bottom.
    private Control BuildLayout()
    {
        var border = ThemeTokens.Find(this, ThemeToken.Border, Brushes.LightGray);

        _statusHost.Padding = new Thickness(10, 4);
        _statusHost.Child = _status;
        _address.TextTrimming = TextTrimming.CharacterEllipsis;

        var address = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = border, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4), Margin = new Thickness(8, 6, 8, 2),
            Child = _address,
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,130,*"),
            Margin = new Thickness(8, 6, 8, 3),
        };
        foreach (var (text, column) in new[] { ("Name", 0), ("Type", 1), ("Data", 2) })
        {
            var label = new TextBlock { [Grid.ColumnProperty] = column, Text = text };
            header.Children.Add(label);
        }
        var headerRule = new Border { Height = 1, Background = border, Margin = new Thickness(8, 0, 8, 0) };

        var values = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(headerRule, Dock.Top);
        values.Children.Add(header);
        values.Children.Add(headerRule);
        values.Children.Add(_values);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,4,*"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        body.Children.Add(_tree);
        body.Children.Add(new GridSplitter
        {
            Width = 4, [Grid.ColumnProperty] = 1,
            Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.BasedOnAlignment,
        });
        body.Children.Add(values);
        Grid.SetColumn(values, 2);
        _values.Margin = new Thickness(8, 0, 0, 0);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(BuildMenu());
        Grid.SetRow(address, 1);
        root.Children.Add(address);
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Grid.SetRow(_statusHost, 3);
        root.Children.Add(_statusHost);
        return root;
    }

    // ---- row visuals ----

    private StackPanel KeyRow(RegKeyNode node)
    {
        var chevron = new Polygon
        {
            Width = 8, Height = 8, Fill = Brushes.Gray,
            Margin = new Thickness(node.Depth * 14 + 4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Points = node.HasChildren ? RightArrow() : [],
        };
        chevron.PointerReleased += (_, e) =>
        {
            if (node.HasChildren) Toggle(node);
            e.Handled = true;
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                chevron,
                new ShellIcon { IconName = "folder", IconSize = 16, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = node.Display, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private static Point[] RightArrow() => [new(2, 0), new(7, 4), new(2, 8)];

    private static Grid ValueRow(RegValueRow row) => new()
    {
        ColumnDefinitions = new ColumnDefinitions("240,130,*"), Margin = new Thickness(8, 2),
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new ShellIcon { IconName = KindIcon(row.Kind), IconSize = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = row.Name.Length == 0 ? "(Default)" : row.Name, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            new TextBlock
            {
                Text = row.Type, [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75, FontSize = 12,
            },
            new TextBlock
            {
                Text = row.Data, [Grid.ColumnProperty] = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };

    // The value-type icons regedit shows per row, as breeze mimetype icons, from closest match down.
    private static string KindIcon(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary or RegistryValueKind.DWordBigEndian
            or RegistryValueKind.ResourceList or RegistryValueKind.FullResourceDescriptor
            or RegistryValueKind.ResourceRequirementsList or RegistryValueKind.None => "application-octet-stream",
        RegistryValueKind.DWord or RegistryValueKind.QWord
            or RegistryValueKind.Int32 or RegistryValueKind.Int64
            or RegistryValueKind.Float or RegistryValueKind.Double or RegistryValueKind.Decimal => "text-x-hex",
        RegistryValueKind.Bool or RegistryValueKind.Guid => "text-x-generic",
        RegistryValueKind.Date or RegistryValueKind.Time or RegistryValueKind.DateTime => "text-calendar",
        RegistryValueKind.Json => "application-json",
        RegistryValueKind.Xml => "application-xml",
        RegistryValueKind.Yaml => "text-x-generic",
        _ => "text-plain",   // strings and links
    };

    private static string NextName(IEnumerable<string> existing, string baseName)
    {
        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? baseName : $"{baseName} #{n}";
            if (!existing.Any(e => e.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }
}

// One key in the sidebar: where it is (hive-relative path, and the same with the HKEY_ root for showing) and
// what it holds. Children are read from the hive only when the key is expanded, and kept across refreshes.
public sealed class RegKeyNode
{
    public string Display { get; }
    public string Path { get; }       // hive-relative; "" is the hive root itself
    public string FullPath { get; }   // with the HKEY_ root in front, as the status bar and SelectPath show it
    public IHive Hive { get; }
    public int Depth { get; }
    public bool Expanded { get; set; }
    public bool Loaded { get; private set; }
    public bool HasChildren { get; private set; }
    public IReadOnlyList<RegKeyNode> Children { get; private set; } = [];

    public RegKeyNode(string display, string path, string fullPath, IHive hive, int depth)
    {
        Display = display;
        Path = path;
        FullPath = fullPath;
        Hive = hive;
        Depth = depth;
    }

    // Whether the expander shows an arrow at all.
    public void Inspect()
    {
        try { HasChildren = Hive.GetSubKeyNames(Path).Count > 0; }
        catch (Exception e) when (e is UnauthorizedAccessException or KeyNotFoundException) { HasChildren = false; }
    }

    internal void SetChildren(IReadOnlyList<RegKeyNode> children, bool loaded)
    {
        Children = children;
        Loaded = loaded;
    }
}

// One value as the list shows it. Null permissions mean the hive would not tell.
public sealed record RegValueRow(string Name, RegistryValueKind Kind, string Type, string Data, PosixPermissions? Permissions);

// The modify dialog: one editor per kind of value, an explanation of the expected text, and the same parsing
// the write will use, so what is accepted here is what the hive will store. Runs modally like the other dialogs.
public sealed class ValueEditWindow : Window
{
    public string? Result { get; private set; }

    public ValueEditWindow(string name, RegistryValueKind kind, string initial, PosixPermissions? permissions)
    {
        Title = name.Length == 0 ? "Edit value" : $"Edit '{name}'";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var kindText = new TextBlock { Text = "Type: " + RegEditModel.KindName(kind), Opacity = 0.75, Margin = new Thickness(0, 4, 0, 8) };
        var nameText = new TextBlock { Text = name.Length == 0 ? "(Default)" : name, Margin = new Thickness(0, 0, 0, 4) };

        Control editor;
        if (kind == RegistryValueKind.Bool)
        {
            var check = new CheckBox { Content = "True", IsChecked = initial.Equals("True", StringComparison.OrdinalIgnoreCase) };
            check.PropertyChanged += (_, e) => { if (e.Property == CheckBox.IsCheckedProperty) check.Content = check.IsChecked == true ? "True" : "False"; };
            editor = check;
            _result = () => check.IsChecked == true ? "True" : "False";
        }
        else
        {
            var box = new TextBox
            {
                Text = initial, AcceptsReturn = AcceptsReturn(kind),
                MinHeight = AcceptsReturn(kind) ? 150 : 0,
                FontFamily = new FontFamily("monospace"),
            };
            editor = box;
        }

        var ok = new Button { Content = "OK", MinWidth = 84, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true };

        void Accept()
        {
            if (_result is { } read)
            {
                var text = read();
                try { RegEditModel.Parse(kind, text); Result = text; }
                catch (ArgumentException e) { Error.Text = e.Message; return; }
            }
            Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };

        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(nameText);
        stack.Children.Add(kindText);
        stack.Children.Add(editor);
        stack.Children.Add(Error);
        if (permissions is { } p)
            stack.Children.Add(permissionsNote(p));
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(buttons);
        Content = stack;
        if (_result is null) _result = () => ((TextBox)editor).Text ?? "";
    }

    private static bool AcceptsReturn(RegistryValueKind kind) => kind is
        RegistryValueKind.MultiString or RegistryValueKind.Json or RegistryValueKind.Yaml or RegistryValueKind.Xml
        or RegistryValueKind.ResourceList or RegistryValueKind.FullResourceDescriptor or RegistryValueKind.ResourceRequirementsList;

    private readonly Func<string>? _result;
    private readonly TextBlock Error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    private static Border permissionsNote(PosixPermissions p) => new()
    {
        Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7,
        Child = new TextBlock { Text = $"Owner {p.Uid}, group {p.Gid}, mode {p.Symbolic} ({p.ModeText})", FontSize = 11 },
    };
}
