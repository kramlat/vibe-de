using Vde.Freedesktop;
using Vibe.Registry;

namespace VdeSession;

public enum RestartPolicy { Never, OnFailure, Always }

// One program of the session. Critical: when it ends the session ends (the compositor).
public sealed record ComponentSpec(string Name, IReadOnlyList<string> Command, RestartPolicy Restart = RestartPolicy.Always,
    bool Critical = false, IReadOnlyList<int>? PermanentFailureCodes = null, string Description = "")
{
    public bool Enabled { get; init; } = true;
}

// What the session runs, in start order, and how the user can change it. The compositor comes first and the rest
// start once it has a Wayland socket. The registry can disable a component or replace its command:
//   HKCU\Software\Vde\Session\Components\<Name>    Enabled (DWORD, 0 disables)    Command (a command line)
public static class SessionComponents
{
    public const string KeyPath = @"Software\Vde\Session\Components";
    public const string WaylandName = "wayland-vde";

    public const string Compositor = "vwm", Chrome = "chrome", Desktop = "desktop", Taskbar = "taskbar", Notifications = "notifications", XEmbedTray = "xembed-tray";

    public static IReadOnlyList<ComponentSpec> Defaults(string waylandName = WaylandName) =>
    [
        new(Compositor, ["vwm", "-w", waylandName], RestartPolicy.Never, Critical: true, Description: "window manager and compositor"),
        new(Chrome, ["vexplore", "--chrome"], Description: "titlebars and window menus for vwm"),
        new(Desktop, ["vexplore", "--desktop"], Description: "wallpaper and desktop icons"),
        new(Taskbar, ["vexplore", "--taskbar"], Description: "taskbar and start button"),
        // exit status 2 = another notification daemon owns the name: that is not worth restarting over
        new(Notifications, ["VDENotify"], RestartPolicy.OnFailure, PermanentFailureCodes: [2], Description: "notification daemon"),
        // the old X11 tray protocol (Steam, Wine, GTK/Qt apps...) as StatusNotifierItems; status 2 = another tray manager owns it
        new(XEmbedTray, ["vde-xembed"], RestartPolicy.OnFailure, PermanentFailureCodes: [2], Description: "X11 system tray (XEmbed) bridge"),
    ];

    public static IReadOnlyList<ComponentSpec> Load(IHive? hive, string waylandName = WaylandName)
    {
        var result = new List<ComponentSpec>();
        foreach (var spec in Defaults(waylandName))
        {
            var key = KeyPath + "\\" + spec.Name;
            var current = spec;
            if (hive is not null)
            {
                if (RegistryBool.Get(hive, key, "Enabled") is { } on)
                    current = current with { Enabled = on };
                if (spec.Critical is false && hive.GetValue(key, "Command") is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } command }
                    && ExecLine.Tokenize(command) is { Count: > 0 } argv)
                    current = current with { Command = argv };
            }
            result.Add(spec.Critical ? spec : current);   // the compositor cannot be switched off or replaced: there would be no session
        }
        return result;
    }
}
