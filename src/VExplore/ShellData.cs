using Vde.Freedesktop;
using Vde.Shell;

namespace VExplore;

// The mime database and the desktop-entry database, parsed once per process. Building either is a walk of
// the XDG data dirs (about 250 ms together with the flatpak applications counted in), which every window
// open and every launch click used to pay again. A slot is re-parsed when it is older than Lifetime, so
// installed or uninstalled apps show up at the latest five minutes later.
internal static class ShellData
{
    private sealed record Slot<T>(T Value, DateTime Loaded);
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static readonly object _lock = new();
    private static Slot<MimeDatabase>? _mime;
    private static readonly Dictionary<string, Slot<DesktopEntryDatabase>> _apps = new(StringComparer.Ordinal);

    public static MimeDatabase Mime()
    {
        lock (_lock)
        {
            if (_mime is not { } mime || DateTime.UtcNow - mime.Loaded > Lifetime)
                _mime = new Slot<MimeDatabase>(new MimeDatabase(), DateTime.UtcNow);
            return _mime.Value;
        }
    }

    // The desktop entries for a locale list: the windows use the environment's locales, launching uses none.
    public static DesktopEntryDatabase Apps(IReadOnlyList<string> locales)
    {
        var key = string.Join('|', locales);
        lock (_lock)
        {
            if (_apps.TryGetValue(key, out var apps) && DateTime.UtcNow - apps.Loaded <= Lifetime)
                return apps.Value;
            var fresh = new Slot<DesktopEntryDatabase>(DesktopEntryDatabase.Load(XdgDirectories.Default, locales), DateTime.UtcNow);
            _apps[key] = fresh;
            if (_apps.Count > 4)
                foreach (var stale in _apps.Where(kv => DateTime.UtcNow - kv.Value.Loaded > Lifetime).Select(kv => kv.Key).ToList())
                    _apps.Remove(stale);
            return fresh.Value;
        }
    }
}
