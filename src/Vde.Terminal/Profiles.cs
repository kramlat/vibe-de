namespace Vde.Terminal;

public enum ShellKind { Other, Bash, PowerShell }

// A way to start a terminal: which program, and whether VDE's shell integration can be loaded into it.
public sealed record ShellProfile(string Name, IReadOnlyList<string> Command, ShellKind Kind = ShellKind.Other, string? Icon = null);

// The profiles a terminal offers and how a shell is started with integration. The user's login shell is first;
// PowerShell (pwsh) and bash appear when installed. The registry can add or replace profiles:
//   HKCU\Software\Vde\Terminal\Profiles\<name>   Command (a command line)    Kind (Bash, PowerShell or Other)
public static class ShellProfiles
{
    public static IReadOnlyList<ShellProfile> Detect(Func<string, string?> env, Func<string, string?> find)
    {
        var profiles = new List<ShellProfile>();
        void Add(string name, string program, ShellKind kind, string? icon = null)
        {
            if (find(program) is { } path && profiles.All(p => p.Command[0] != path))
                profiles.Add(new ShellProfile(name, [path], kind, icon));
        }
        var login = env("SHELL");
        if (login is { Length: > 0 } && find(login) is { } loginPath)
            profiles.Add(new ShellProfile(Path.GetFileName(loginPath), [loginPath], KindOf(loginPath)));
        Add("PowerShell", "pwsh", ShellKind.PowerShell, "utilities-terminal");
        Add("bash", "bash", ShellKind.Bash);
        Add("zsh", "zsh", ShellKind.Other);
        Add("fish", "fish", ShellKind.Other);
        if (profiles.Count == 0) profiles.Add(new ShellProfile("sh", ["/bin/sh"]));
        return profiles;
    }

    public static ShellKind KindOf(string program)
    {
        var name = Path.GetFileName(program);
        return name switch { "pwsh" or "powershell" => ShellKind.PowerShell, "bash" => ShellKind.Bash, _ => ShellKind.Other };
    }

    // The command line and environment that start the profile's shell with integration loaded, when there is an
    // integration script for it. `integrationDir` is where vde-integration.* are installed.
    public static (IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Environment) Launch(ShellProfile profile, string? integrationDir)
    {
        var env = new Dictionary<string, string> { ["VDE_TERMINAL"] = "vterminal" };
        var command = profile.Command.ToList();
        if (integrationDir is null || command.Count != 1)   // a profile with its own arguments is the user's to manage
            return (command, env);
        switch (profile.Kind)
        {
            case ShellKind.PowerShell when File.Exists(Path.Combine(integrationDir, "vde-integration.ps1")):
                env["VDE_TERMINAL_INTEGRATION"] = "1";
                // -NoExit with -Command: the user's profile loads first, then this
                command.AddRange(["-NoLogo", "-NoExit", "-Command", $". '{Path.Combine(integrationDir, "vde-integration.ps1").Replace("'", "''")}'"]);
                break;
            case ShellKind.Bash when File.Exists(Path.Combine(integrationDir, "vde-integration.bash")):
                env["VDE_TERMINAL_INTEGRATION"] = "1";
                command.AddRange(["--init-file", Path.Combine(integrationDir, "vde-integration.bash")]);
                break;
        }
        return (command, env);
    }

    public static string? FindIntegrationDir()
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("VDE_DATA") is { Length: > 0 } data) dirs.Add(Path.Combine(data, "terminal"));
        dirs.Add("/usr/share/vde/terminal");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            dirs.Add(Path.Combine(d.FullName, "data", "terminal"));
        return dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "vde-integration.ps1")));
    }

    public static string? FindOnPath(string name, Func<string, string?> env)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (env("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
