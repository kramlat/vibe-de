using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Components;
using Vde.Shell;
using Vde.Shell.Controls;

namespace Vde.Shell.ComDlg;

// One tab of the Properties window.
public interface IPropertyPage
{
    string Title { get; }
    int Order { get; }
    Control Content { get; }
    bool IsDirty { get; }
    // Returns false to keep the window open (the page has explained why).
    bool Apply();
}

public sealed class PropertySheetContext
{
    public required PropertyTarget Target { get; init; }
    public FileTypes? Types { get; init; }
    public UserNames Names { get; init; } = UserNames.System;
    public FileActions? Actions { get; init; }                       // used to rename
    public PropertyHandlers? Handlers { get; init; }                 // tabs from registered components
    public ComponentLoader? Loader { get; init; }
    public Func<string, string?>? OpensWith { get; init; }           // mime type -> name of the application that opens it
    public Action<string>? ChangeOpensWith { get; init; }            // mime type: the host shows its "Open with" chooser
    // Providers that live in the host itself (the shell's own tabs). They need no registration, and a registry
    // handler with the same CLSID is not loaded a second time.
    public IReadOnlyList<(string clsid, IPropertyPageProvider provider)> BuiltInProviders { get; init; } = [];
}

// The Properties window: General, Permissions and Details, and whatever tabs registered components add.
// Works for one item or for a selection (then values that differ show as mixed).
public sealed class PropertySheetWindow : Window
{
    private readonly List<IPropertyPage> _pages = new();
    private readonly TabControl _tabs = new();

    public PropertySheetWindow(PropertySheetContext context)
    {
        Context = context;
        var props = context.Target.Paths.Select(p => FileProperties.Read(p, context.Types, context.Names)).ToList();
        Items = props;
        Title = props.Count == 1 ? props[0].Name + " Properties" : $"{props.Count} items Properties";
        Width = 440; Height = 560; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pages.Add(new GeneralPage(context, props));
        _pages.Add(new DetailsPage(props));
        AddHandlerPages(context, props);
        _pages.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var page in _pages)
            _tabs.Items.Add(new TabItem { Header = page.Title, Content = page.Content });

        var ok = DialogHost.Button("OK", () => { if (ApplyAll()) Close(); }, isDefault: true);
        var cancel = DialogHost.Button("Cancel", () => Close(), isCancel: true);
        var apply = DialogHost.Button("Apply", () => ApplyAll());
        var root = new DockPanel { Margin = new Thickness(10) };
        var buttons = DialogHost.ButtonRow(ok, cancel, apply);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);
        Content = root;
    }

    public PropertySheetContext Context { get; }
    public IReadOnlyList<FileProperties> Items { get; }
    public IReadOnlyList<IPropertyPage> Pages => _pages;
    public IReadOnlyList<string> Problems => _problems;
    private readonly List<string> _problems = new();

    // A page of this type: a built-in tab, or the object a handler gave as the Tag of its tab.
    public T Page<T>() where T : class, IPropertyPage
        => _pages.OfType<T>().Concat(_pages.OfType<HandlerPage>().Select(p => p.Info.Tag).OfType<T>()).Single();

    // OK and Apply: every changed page, stopping at the first that refuses.
    public bool ApplyAll()
    {
        foreach (var page in _pages.Where(p => p.IsDirty))
            if (!page.Apply())
                return false;
        return true;
    }

    // Tabs from registered components. A handler that is not trusted, will not load or throws adds a note
    // instead of a tab: a broken plugin must not stop the window opening.
    private void AddHandlerPages(PropertySheetContext context, IReadOnlyList<FileProperties> props)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, provider) in context.BuiltInProviders)
        {
            seen.Add(clsid);
            try { foreach (var page in provider.GetPages(context.Target)) _pages.Add(new HandlerPage(page)); }
            catch (Exception e) when (e is not OutOfMemoryException) { _problems.Add($"{provider.GetType().Name} failed: {e.Message}"); }
        }

        if (context.Handlers is null || context.Loader is null)
            return;
        var first = props[0];
        var (loadable, refused) = context.Handlers.Split(first.FullPath, first.IsDirectory);
        loadable = loadable.Where(r => !seen.Contains(r.Component.Clsid)).ToList();
        foreach (var r in refused)
            _problems.Add($"{r.Component.ClassName} was not loaded: {r.Trust.Reason}");

        foreach (var registration in loadable)
            try
            {
                var provider = context.Loader.Get<IPropertyPageProvider>(registration);
                foreach (var page in provider.GetPages(context.Target))
                    _pages.Add(new HandlerPage(page));
            }
            catch (Exception e) when (e is not OutOfMemoryException)   // plugin code: whatever it throws is its problem, never the sheet's
            {
                _problems.Add(e is ComponentLoadException ? e.Message : $"{registration.Component.ClassName} failed: {e.Message}");
            }
    }
}

public sealed class HandlerPage(PropertyPageInfo info) : IPropertyPage
{
    public PropertyPageInfo Info => info;
    private Control? _content;
    public string Title => info.Title;
    public int Order => info.Order;
    public Control Content => _content ??= info.CreateContent() as Control ?? new TextBlock { Text = "This page could not be shown." };
    public bool IsDirty => info.IsDirty?.Invoke() ?? info.Apply is not null;
    public bool Apply()
    {
        try { return info.Apply?.Invoke() ?? true; }
        catch (Exception) { return false; }   // a plugin that throws keeps the sheet open rather than crashing the shell
    }
}

public sealed class GeneralPage : IPropertyPage
{
    private readonly PropertySheetContext _context;
    private readonly IReadOnlyList<FileProperties> _props;
    private readonly TextBox _name = new() { MinWidth = 260 };
    private readonly CheckBox _readOnly = new() { Content = "Read-only", IsThreeState = true };
    private readonly TextBlock _size = new(), _contains = new(), _error = new() { Foreground = Brushes.IndianRed, IsVisible = false };
    private readonly bool? _originalReadOnly;

    public GeneralPage(PropertySheetContext context, IReadOnlyList<FileProperties> props)
    {
        _context = context;
        _props = props;
        var first = props[0];
        bool single = props.Count == 1;

        _name.Text = single ? first.Name : $"{props.Count} items";
        _name.IsReadOnly = !single || context.Actions is null;

        var states = props.Select(p => !p.Mode.HasFlag(UnixFileMode.UserWrite) && !p.Mode.HasFlag(UnixFileMode.GroupWrite) && !p.Mode.HasFlag(UnixFileMode.OtherWrite)).Distinct().ToList();
        _originalReadOnly = states.Count == 1 ? states[0] : null;
        _readOnly.IsChecked = _originalReadOnly;

        var type = props.Select(p => p.TypeDescription).Distinct().Count() == 1 ? first.TypeDescription : "Multiple types";
        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
                    { new ShellIcon { IconNames = first.IsDirectory ? ["folder"] : context.Types?.Describe(first.FullPath).IconNames ?? ["text-x-generic"], IconSize = 48 }, _name } },
                Row("Type:", type),
                Row("Location:", props.Select(p => p.Folder).Distinct().Count() == 1 ? first.Folder : "Various locations"),
                Row("Size:", _size), Row("Contains:", _contains),
                new Separator(),
                Row("Modified:", single ? first.ModifiedUtc.ToLocalTime().ToString("f") : ""),
                Row("Accessed:", single ? first.AccessedUtc.ToLocalTime().ToString("f") : ""),
                Row("Changed:", single ? first.ChangedUtc.ToLocalTime().ToString("f") : ""),
                new Separator(),
                OpensWithRow(first),
                _readOnly,
                _error,
            },
        };
        _size.Text = "Calculating…";
        _contains.Text = props.Any(p => p.IsDirectory) ? "Calculating…" : "";
        SizeTask = LoadSizeAsync();
    }

    private async Task LoadSizeAsync()
    {
        // Started on the UI thread, so everything after the await resumes there: Show never needs marshalling,
        // and the task is complete only once the label shows the result.
        var result = await FolderSizer.MeasureAsync(_props.Select(p => p.FullPath), new Progress<FolderSize>(Show));
        Show(result);
    }

    public string Title => "General";
    public int Order => 0;
    public Control Content { get; }
    public Task SizeTask { get; }
    public string Name { get => _name.Text ?? ""; set => _name.Text = value; }
    public bool? ReadOnly { get => _readOnly.IsChecked; set => _readOnly.IsChecked = value; }
    public string SizeText => _size.Text ?? "";
    public string ContainsText => _contains.Text ?? "";
    public string Error => _error.IsVisible ? _error.Text ?? "" : "";

    public bool IsDirty => (_props.Count == 1 && Name != _props[0].Name) || ReadOnly != _originalReadOnly;

    public bool Apply()
    {
        try
        {
            if (ReadOnly != _originalReadOnly && ReadOnly is { } on)
            {
                var writes = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
                foreach (var p in _props)
                    PermissionEditor.Apply(p.FullPath, on ? UnixFileMode.None : UnixFileMode.UserWrite, on ? writes : UnixFileMode.None, recursive: false);
            }
            if (_props.Count == 1 && Name != _props[0].Name)
            {
                if (FileDialogRules.NameProblem(Name) is { } problem) return Fail(problem);
                if (_context.Actions is null) return Fail("Renaming is not available here.");
                _context.Actions.Rename(_props[0].FullPath, Name);
            }
            _error.IsVisible = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(e.Message); }
    }

    private bool Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
        return false;
    }

    private void Show(FolderSize s)
    {
        void Apply()
        {
            _size.Text = ByteFormat.Format(s.Bytes) + (s.Bytes >= 1024 ? $" ({s.Bytes:N0} bytes)" : "") + (s.Complete ? "" : " …");
            if (_props.Any(p => p.IsDirectory))
                _contains.Text = $"{s.Files:N0} files, {Math.Max(0, s.Folders - _props.Count(p => p.IsDirectory)):N0} folders";
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply(); else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

    private Control OpensWithRow(FileProperties first)
    {
        var mime = _props.Select(p => p.MimeType).Distinct().Count() == 1 ? first.MimeType : null;
        var app = mime is null || _props.Any(p => p.IsDirectory) ? null : _context.OpensWith?.Invoke(mime);
        var change = new Button { Content = "Change…", IsEnabled = mime is not null && _context.ChangeOpensWith is not null };
        change.Click += (_, _) => _context.ChangeOpensWith?.Invoke(mime!);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = app is not null || change.IsEnabled, Children = { new TextBlock { Text = "Opens with:", Width = 80 }, new TextBlock { Text = app ?? "(none)", VerticalAlignment = VerticalAlignment.Center }, change } };
    }

    private static Control Row(string label, string value) => Row(label, new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
    private static Control Row(string label, TextBlock value)
        => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = label, Width = 80 }, value } };
}

public sealed class PermissionsPage : IPropertyPage
{
    private static readonly (string label, UnixFileMode r, UnixFileMode w, UnixFileMode x)[] Classes =
    [
        ("Owner", UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute),
        ("Group", UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute),
        ("Others", UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute),
    ];

    private readonly IReadOnlyList<FileProperties> _props;
    private readonly Dictionary<UnixFileMode, CheckBox> _boxes = new();
    private readonly Dictionary<UnixFileMode, bool?> _original = new();
    private readonly CheckBox _recursive = new() { Content = "Apply to everything inside" };
    private readonly ComboBox _group = new() { MinWidth = 180 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, IsVisible = false };
    private readonly IReadOnlyList<(uint gid, string name)> _groups;
    private readonly uint? _originalGid;

    private readonly ElevationOptions? _elevation;
    private readonly Func<string, string?>? _find;
    private readonly TextBlock _ownerLabel = new(), _info = new() { IsVisible = false };
    private readonly Button _takeOwnership = new() { Content = "Take ownership…" };

    public PermissionsPage(IReadOnlyList<(uint gid, string name)> myGroups, IReadOnlyList<FileProperties> props,
        ElevationOptions? elevation = null, Func<string, string?>? findExecutable = null)
    {
        _elevation = elevation;
        _find = findExecutable;
        _props = props;
        var owners = props.Select(p => p.Owner).Distinct().ToList();
        var groups = props.Select(p => p.Group).Distinct().ToList();
        _originalGid = props.Select(p => p.Gid).Distinct().Count() == 1 ? props[0].Gid : null;
        _groups = myGroups;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,70,70,70"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 4 };
        for (int c = 0; c < 3; c++)
        {
            var header = new TextBlock { Text = new[] { "Read", "Write", "Execute" }[c], FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(header, c + 1);
            grid.Children.Add(header);
        }
        for (int r = 0; r < Classes.Length; r++)
        {
            var (label, rd, wr, ex) = Classes[r];
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, r + 1);
            grid.Children.Add(name);
            foreach (var (bit, column) in new[] { (rd, 1), (wr, 2), (ex, 3) })
            {
                var states = props.Select(p => p.Mode.HasFlag(bit)).Distinct().ToList();
                bool? state = states.Count == 1 ? states[0] : null;   // mixed when the items differ
                var box = new CheckBox { IsThreeState = true, IsChecked = state };
                _boxes[bit] = box;
                _original[bit] = state;
                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, column);
                grid.Children.Add(box);
            }
        }

        _group.ItemsSource = _groups.Select(g => g.name).ToList();
        _group.SelectedIndex = _groups.ToList().FindIndex(g => g.gid == _originalGid);
        _ownerLabel.Text = "Owner: " + (owners.Count == 1 ? owners[0] : "various");
        _ownerLabel.VerticalAlignment = VerticalAlignment.Center;
        // Only offered where it would change something, and where there is a way to ask for the rights it needs.
        _takeOwnership.IsVisible = elevation is not null && props.Any(p => p.Uid != elevation.Identity.Uid);
        _takeOwnership.Click += async (_, _) => await TakeOwnershipAsync();
        bool anyFolder = props.Any(p => p.IsDirectory);
        _recursive.IsVisible = anyFolder;

        Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _ownerLabel, _takeOwnership } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Group:", VerticalAlignment = VerticalAlignment.Center }, groups.Count == 1 && _groups.Count == 0 ? new TextBlock { Text = groups[0] } : _group } },
                grid, _recursive,
                new TextBlock { Text = props.Count == 1 ? $"Mode {props[0].ModeOctal}  ({props[0].ModeText})" : "", Opacity = 0.6 },
                _error, _info,
            },
        };
    }

    public string Title => "Permissions";
    public bool CanTakeOwnership => _takeOwnership.IsVisible;
    public string OwnerText => _ownerLabel.Text ?? "";
    public string Info => _info.IsVisible ? _info.Text ?? "" : "";

    // "Take ownership": the shell shows the chown command and asks for the password (UAC-like). Cancelling or being
    // refused changes nothing and says so here. Applies to the items and, with the box ticked, to everything inside.
    public async Task<bool> TakeOwnershipAsync()
    {
        if (_elevation is null) return Say("Taking ownership is not available here.", error: true);
        try
        {
            await Ownership.TakeAsync(_props.Select(p => p.FullPath).ToList(), Recursive && _props.Any(p => p.IsDirectory), _elevation, _find);
        }
        catch (OperationCanceledException) { Say("Cancelled. Nothing was changed.", error: false); return false; }
        catch (Exception e) when (e is ElevationDeniedException or InvalidOperationException or FileNotFoundException) { return Say(e.Message, error: true); }
        var owner = FileProperties.Read(_props[0].FullPath).Owner;
        _ownerLabel.Text = "Owner: " + owner;
        return Say("Ownership was changed.", error: false);
    }

    private bool Say(string text, bool error)
    {
        _info.Text = text;
        _info.IsVisible = true;
        _info.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen;
        return !error;
    }
    public int Order => 10;
    public Control Content { get; }
    public bool Recursive { get => _recursive.IsChecked == true; set => _recursive.IsChecked = value; }

    // The state of one permission checkbox: true, false or null (mixed).
    public bool? Get(UnixFileMode bit) => _boxes[bit].IsChecked;
    public void Set(UnixFileMode bit, bool on) => _boxes[bit].IsChecked = on;
    public void SelectGroup(string name) => _group.SelectedIndex = _groups.ToList().FindIndex(g => g.name == name);
    public string Error => _error.IsVisible ? _error.Text ?? "" : "";

    public bool IsDirty => _boxes.Any(kv => kv.Value.IsChecked != _original[kv.Key]) || SelectedGid is { } g && g != _originalGid || Recursive;
    private uint? SelectedGid => _group.SelectedIndex >= 0 && _group.SelectedIndex < _groups.Count ? _groups[_group.SelectedIndex].gid : null;

    // Only what the user changed is applied: a mixed box left alone leaves each item's own bit as it was.
    public bool Apply()
    {
        var set = UnixFileMode.None; var clear = UnixFileMode.None;           // what the user changed
        var matchSet = UnixFileMode.None; var matchClear = UnixFileMode.None; // the sheet as shown, for "apply to everything inside"
        foreach (var (bit, box) in _boxes)
        {
            if (box.IsChecked != _original[bit] && box.IsChecked is { } on)
                if (on) set |= bit; else clear |= bit;
            if (box.IsChecked is { } shown)
                if (shown) matchSet |= bit; else matchClear |= bit;   // a mixed box says nothing, so it is left as each item has it
        }
        try
        {
            foreach (var p in _props)
            {
                if (Recursive && p.IsDirectory)
                    PermissionEditor.Apply(p.FullPath, matchSet, matchClear, recursive: true);
                else if (set != UnixFileMode.None || clear != UnixFileMode.None)
                    PermissionEditor.Apply(p.FullPath, set, clear, recursive: false);
                if (SelectedGid is { } gid && gid != p.Gid)
                    PermissionEditor.SetGroup(p.FullPath, gid);
            }
            _error.IsVisible = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _error.Text = e.Message;
            _error.IsVisible = true;
            return false;
        }
    }
}

public sealed class DetailsPage : IPropertyPage
{
    public DetailsPage(IReadOnlyList<FileProperties> props)
    {
        var rows = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        if (props.Count != 1)
            rows.Children.Add(new TextBlock { Text = "Select a single item to see its details." });
        else
        {
            var p = props[0];
            foreach (var (k, v) in Details(p))
                rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = k + ":", Width = 110, Opacity = 0.7 }, new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 } } });
        }
        Content = new ScrollViewer { Content = rows };
        Rows = props.Count == 1 ? Details(props[0]) : [];
    }

    public string Title => "Details";
    public int Order => 20;
    public Control Content { get; }
    public bool IsDirty => false;
    public bool Apply() => true;
    public IReadOnlyList<(string key, string value)> Rows { get; }

    internal static IReadOnlyList<(string, string)> Details(FileProperties p)
    {
        var rows = new List<(string, string)>
        {
            ("Name", p.Name), ("Full path", p.FullPath), ("Type", p.TypeDescription), ("Content type", p.MimeType),
            ("Size", p.IsDirectory ? "" : $"{p.Size:N0} bytes"), ("Owner", $"{p.Owner} ({p.Uid})"), ("Group", $"{p.Group} ({p.Gid})"),
            ("Permissions", $"{p.ModeOctal}  {p.ModeText}"), ("Inode", p.Inode.ToString()), ("Hard links", p.HardLinks.ToString()),
            ("Modified", p.ModifiedUtc.ToLocalTime().ToString("F")), ("Changed", p.ChangedUtc.ToLocalTime().ToString("F")),
        };
        if (p.LinkTarget is { } target)
            rows.Insert(3, ("Link target", target));
        return rows.Where(r => r.Item2.Length > 0).ToList();
    }
}

// The POSIX permissions tab (owner, group, rwx for owner, group and others), as a property sheet handler: just as
// Explorer's Security tab comes from a handler and not from the Properties box itself. The shell's own handler
// (VExplore) includes it; any host can add it the same way.
public sealed class PosixPermissionsPages(IReadOnlyList<(uint gid, string name)>? myGroups = null, UserNames? names = null,
    ElevationOptions? elevation = null, Func<string, string?>? findExecutable = null) : IPropertyPageProvider
{
    public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target)
    {
        var names1 = names ?? UserNames.System;
        var groups = myGroups ?? Vibe.Registry.RegistryIdentity.Current.Groups.Select(g => (g, names1.Group(g))).OrderBy(g => g.Item2).ToList();
        List<FileProperties> props;
        try { props = target.Paths.Select(p => FileProperties.Read(p, null, names1)).ToList(); }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return []; }   // nothing to show for something that is gone
        if (props.Count == 0) return [];

        var page = new PermissionsPage(groups, props, elevation, findExecutable);
        return [new PropertyPageInfo(page.Title, () => page.Content, page.Apply, page.Order, () => page.IsDirty, page)];
    }
}
