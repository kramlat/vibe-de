namespace VdeSession;

// The environment every program in the session shares.
public static class SessionEnvironment
{
    // Variables the session sets for itself. Existing values for the desktop-identity ones are replaced: this is a VDE session.
    public static IReadOnlyDictionary<string, string> Base(Func<string, string?> env)
    {
        var vars = new Dictionary<string, string>
        {
            ["XDG_SESSION_TYPE"] = "wayland",
            ["XDG_CURRENT_DESKTOP"] = "VDE",
            ["XDG_SESSION_DESKTOP"] = "vde",
            ["DESKTOP_SESSION"] = "vde",
            // toolkits: use Wayland where they can, X11 (through XWayland) where they cannot
            ["QT_QPA_PLATFORM"] = "wayland;xcb",
            ["GDK_BACKEND"] = "wayland,x11",
            ["MOZ_ENABLE_WAYLAND"] = "1",
            ["SDL_VIDEODRIVER"] = "wayland,x11",
            ["_JAVA_AWT_WM_NONREPARENTING"] = "1",
            ["ELECTRON_OZONE_PLATFORM_HINT"] = "auto",
        };
        // the cursor size and theme follow the registry later; a sane default keeps toolkits from guessing
        if (string.IsNullOrEmpty(env("XCURSOR_SIZE"))) vars["XCURSOR_SIZE"] = "24";
        return vars;
    }

    // What the session manager passes on: its own environment, then the session's, then what the compositor reported
    // (WAYLAND_DISPLAY, DISPLAY). The display variables of any session this one was started from are dropped first.
    public static Dictionary<string, string> For(IReadOnlyDictionary<string, string> inherited, IReadOnlyDictionary<string, string> session,
        IReadOnlyDictionary<string, string>? compositor)
    {
        var merged = new Dictionary<string, string>(inherited);
        merged.Remove("WAYLAND_DISPLAY");
        merged.Remove("DISPLAY");
        foreach (var (k, v) in session) merged[k] = v;
        if (compositor is not null)
            foreach (var (k, v) in compositor) merged[k] = v;
        return merged;
    }

    // Reads KEY=value lines (what vwm writes), ignoring anything else.
    public static IReadOnlyDictionary<string, string> ParseEnvFile(string text)
    {
        var vars = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.IndexOf('=') is > 0 and var eq && line[..eq] is "WAYLAND_DISPLAY" or "DISPLAY" && line[(eq + 1)..].Length > 0)
                vars[line[..eq]] = line[(eq + 1)..];
        return vars;
    }

    // The variables that D-Bus-activated and systemd user services need to find the display too.
    public static readonly string[] Exported =
    [
        "WAYLAND_DISPLAY", "DISPLAY", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION",
        "QT_QPA_PLATFORM", "GDK_BACKEND", "MOZ_ENABLE_WAYLAND", "XCURSOR_SIZE",
    ];
}
