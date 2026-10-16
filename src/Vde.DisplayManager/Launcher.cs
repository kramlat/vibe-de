namespace Vde.DisplayManager;

// Everything needed to start a process as someone else: decided here (and tested), carried out by the daemon.
public sealed record LaunchPlan(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory,
    string UserName, string Tty);

// How the pieces of a session are put together.
public static class SessionPlans
{
    public const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/bin:/usr/lib/vde";

    // The user's desktop. The command runs through the account's login shell setting up the environment the way a
    // text login would (/etc/profile, ~/.profile), then `exec`s the session. The environment the greeter asked for
    // goes on top of what PAM and the account give.
    public static LaunchPlan Session(PendingSession pending, IReadOnlyDictionary<string, string> pamEnvironment, int vt, string launcher, string? desktopNames = null)
    {
        var env = new Dictionary<string, string>
        {
            ["HOME"] = pending.User.Home, ["USER"] = pending.User.Name, ["LOGNAME"] = pending.User.Name,
            ["SHELL"] = pending.User.Shell, ["PATH"] = DefaultPath, ["LANG"] = "C.UTF-8",
            ["XDG_SEAT"] = "seat0", ["XDG_VTNR"] = vt.ToString(), ["XDG_SESSION_TYPE"] = "wayland", ["XDG_SESSION_CLASS"] = "user",
        };
        if (desktopNames is not null) { env["XDG_CURRENT_DESKTOP"] = desktopNames; env["XDG_SESSION_DESKTOP"] = desktopNames.Split(':')[0]; }
        foreach (var (k, v) in pamEnvironment) env[k] = v;   // pam_systemd's XDG_SESSION_ID and XDG_RUNTIME_DIR among them
        foreach (var entry in pending.Environment)
        {
            var eq = entry.IndexOf('=');
            env[entry[..eq]] = entry[(eq + 1)..];
        }
        var tty = $"/dev/tty{vt}";
        // /bin/sh -l: a POSIX shell, so whatever the user's own shell is (fish, nushell) does not matter
        var command = new List<string> { launcher, tty, "-C", pending.User.Home, "--", "/bin/sh", "-lc", "exec \"$@\"", "vde-session" };
        command.AddRange(pending.Command);
        return new LaunchPlan(command, env, pending.User.Home, pending.User.Name, tty);
    }

    // The login screen: a compositor with the greeter in it, as the unprivileged greeter account.
    public static LaunchPlan Greeter(string greeterUser, string home, IReadOnlyList<string> greeterCommand, string socketPath, int vt, string launcher, IReadOnlyDictionary<string, string>? pamEnvironment = null)
    {
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home, ["USER"] = greeterUser, ["LOGNAME"] = greeterUser, ["PATH"] = DefaultPath, ["LANG"] = "C.UTF-8",
            ["XDG_SEAT"] = "seat0", ["XDG_VTNR"] = vt.ToString(), ["XDG_SESSION_TYPE"] = "wayland", ["XDG_SESSION_CLASS"] = "greeter",
            ["XDG_CURRENT_DESKTOP"] = "VDE", ["VDEDM_SOCK"] = socketPath, ["GREETD_SOCK"] = socketPath,
        };
        if (pamEnvironment is not null) foreach (var (k, v) in pamEnvironment) env[k] = v;
        var tty = $"/dev/tty{vt}";
        var command = new List<string> { launcher, tty, "-C", home, "--" };
        command.AddRange(greeterCommand);
        return new LaunchPlan(command, env, home, greeterUser, tty);
    }
}
