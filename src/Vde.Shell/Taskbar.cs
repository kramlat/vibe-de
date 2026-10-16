using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.Shell;

// One top-level window of some application, as the compositor reports it.
public sealed record WindowInfo(string Id, string Title, string AppId, bool IsActive = false, bool IsMinimized = false);

// Where the taskbar learns about windows and how it acts on them. The compositor (or its protocol client) implements it.
public interface IWindowSource
{
    IReadOnlyList<WindowInfo> Windows { get; }
    event EventHandler? Changed;
    void Activate(string id);
    void Minimize(string id);
    void Close(string id);
}

// Nothing is reported: used until a compositor connection exists.
public sealed class NoWindowSource : IWindowSource
{
    public IReadOnlyList<WindowInfo> Windows => [];
    public event EventHandler? Changed { add { } remove { } }
    public void Activate(string id) { }
    public void Minimize(string id) { }
    public void Close(string id) { }
}

public enum TaskbarEdge { Bottom, Top, Left, Right }

public enum TaskbarAlignment { Center, Start }

// One app on the taskbar: pinned, running, or both. Several windows of an app share a button.
public sealed record TaskbarButton(string Key, string Name, IReadOnlyList<string> IconNames, DesktopEntry? App, bool Pinned, IReadOnlyList<WindowInfo> Windows)
{
    public bool IsRunning => Windows.Count > 0;
    public bool IsActive => Windows.Any(w => w.IsActive);
}

public enum TaskbarClickResult { None, Launched, Activated, Minimized, ChooseWindow }

// The taskbar's state: which apps are pinned (registry), which windows are open, where it sits.
//   HKCU\Software\Vde\Taskbar   Pinned (multi-string of desktop ids), Edge (Bottom/Top/Left/Right), Alignment (Center/Start)
public sealed class TaskbarModel : IDisposable
{
    public const string KeyPath = @"Software\Vde\Taskbar";

    private readonly IHive _hive;
    private readonly DesktopEntryDatabase _apps;
    private readonly IWindowSource _windows;
    private readonly Action<DesktopEntry> _launch;

    public event EventHandler? Changed;

    public TaskbarModel(IHive hive, DesktopEntryDatabase apps, IWindowSource windows, Action<DesktopEntry> launch)
    {
        _hive = hive;
        _apps = apps;
        _windows = windows;
        _launch = launch;
        _hive.Changed += OnHiveChanged;
        _windows.Changed += OnWindowsChanged;
    }

    public void Dispose()
    {
        _hive.Changed -= OnHiveChanged;
        _windows.Changed -= OnWindowsChanged;
    }

    public TaskbarEdge Edge
    {
        get => Enum.TryParse<TaskbarEdge>(ReadString("Edge"), true, out var e) ? e : TaskbarEdge.Bottom;
        set => _hive.SetValue(KeyPath, "Edge", RegistryValueKind.String, value.ToString());
    }

    public TaskbarAlignment Alignment
    {
        get => Enum.TryParse<TaskbarAlignment>(ReadString("Alignment"), true, out var a) ? a : TaskbarAlignment.Center;
        set => _hive.SetValue(KeyPath, "Alignment", RegistryValueKind.String, value.ToString());
    }

    public bool IsVertical => Edge is TaskbarEdge.Left or TaskbarEdge.Right;

    public IReadOnlyList<string> PinnedIds
        => _hive.GetValue(KeyPath, "Pinned") is { Kind: RegistryValueKind.MultiString } v ? [.. (string[])v.Data!] : [];

    public void Pin(string desktopId)
    {
        var ids = PinnedIds.ToList();
        if (_apps.Find(desktopId) is null || ids.Contains(desktopId, StringComparer.OrdinalIgnoreCase)) return;
        ids.Add(desktopId);
        _hive.SetValue(KeyPath, "Pinned", RegistryValueKind.MultiString, ids.ToArray());
    }

    public void Unpin(string desktopId)
        => _hive.SetValue(KeyPath, "Pinned", RegistryValueKind.MultiString, PinnedIds.Where(i => !string.Equals(i, desktopId, StringComparison.OrdinalIgnoreCase)).ToArray());

    // Pinned apps in their pinned order, then running apps that are not pinned, in the order they appeared.
    public IReadOnlyList<TaskbarButton> Buttons()
    {
        var remaining = _windows.Windows.ToList();
        var buttons = new List<TaskbarButton>();
        foreach (var id in PinnedIds)
        {
            if (_apps.Find(id) is not { } app) continue;
            var mine = remaining.Where(w => Matches(app, w.AppId)).ToList();
            remaining.RemoveAll(mine.Contains);
            buttons.Add(new TaskbarButton(app.Id, app.Name, IconsOf(app), app, true, mine));
        }
        foreach (var group in remaining.GroupBy(w => w.AppId, StringComparer.OrdinalIgnoreCase))
        {
            var app = _apps.Entries.FirstOrDefault(e => Matches(e, group.Key));
            buttons.Add(new TaskbarButton(app?.Id ?? "window:" + group.Key, app?.Name ?? group.First().Title,
                app is null ? [group.Key.ToLowerInvariant(), "application-x-executable"] : IconsOf(app), app, false, group.ToList()));
        }
        return buttons;
    }

    // What a click does: start a pinned app that is not running; with one window, raise it (or minimize it if it is
    // already in front); with several, ask the caller to let the user pick.
    public TaskbarClickResult Click(TaskbarButton button)
    {
        if (!button.IsRunning)
        {
            if (button.App is null) return TaskbarClickResult.None;
            _launch(button.App);
            return TaskbarClickResult.Launched;
        }
        if (button.Windows.Count > 1) return TaskbarClickResult.ChooseWindow;
        var w = button.Windows[0];
        if (w.IsActive && !w.IsMinimized)
        {
            _windows.Minimize(w.Id);
            return TaskbarClickResult.Minimized;
        }
        _windows.Activate(w.Id);
        return TaskbarClickResult.Activated;
    }

    public void ActivateWindow(WindowInfo window) => _windows.Activate(window.Id);
    public void CloseWindow(WindowInfo window) => _windows.Close(window.Id);

    // A new window of the app, from the button's menu.
    public void LaunchNew(TaskbarButton button)
    {
        if (button.App is not null) _launch(button.App);
    }

    private static bool Matches(DesktopEntry app, string appId)
    {
        var id = app.Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? app.Id[..^8] : app.Id;
        return string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)
            || (app.StartupWmClass is { Length: > 0 } wm && string.Equals(wm, appId, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> IconsOf(DesktopEntry app) => app.Icon is { Length: > 0 } icon ? [icon, "application-x-executable"] : ["application-x-executable"];

    private string? ReadString(string name) => _hive.GetValue(KeyPath, name) is { Kind: RegistryValueKind.String } v ? v.Data as string : null;

    private void OnHiveChanged(object? sender, RegistryChange change)
    {
        if (change.IsUnder(KeyPath)) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowsChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
