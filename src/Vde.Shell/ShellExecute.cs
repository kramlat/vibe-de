using System.Diagnostics;
using Vde.Freedesktop;

namespace Vde.Shell;

public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

public interface IProcessLauncher
{
    void Start(ProcessRequest request);
}

// Starts a detached process: the shell never waits for what it launches.
public sealed class ProcessLauncher : IProcessLauncher
{
    public void Start(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var arg in request.Arguments)
            info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
    }
}

// Nothing is registered for this type: the caller should offer an "Open with" choice.
public sealed class NoAssociationException : Exception
{
    public string MimeType { get; }

    public NoAssociationException(string target, string mimeType)
        : base($"No application is associated with {mimeType} ({target}).")
        => MimeType = mimeType;
}

// Opens files, folders, URLs and applications the way a user expects a double-click to.
public sealed class ShellExecute
{
    private static readonly (string Name, string[] Prefix)[] Terminals =
    [
        ("konsole", ["-e"]),
        ("gnome-terminal", ["--"]),
        ("xfce4-terminal", ["-x"]),
        ("kitty", []),
        ("foot", []),
        ("alacritty", ["-e"]),
        ("wezterm", ["start", "--"]),
        ("xterm", ["-e"]),
    ];

    private readonly FileAssociations _associations;
    private readonly MimeDatabase _mime;
    private readonly IProcessLauncher _launcher;
    private readonly Func<string, string?> _env;
    private readonly IReadOnlyList<string> _locales;

    public ShellExecute(
        FileAssociations associations, MimeDatabase mime, IProcessLauncher? launcher = null,
        Func<string, string?>? env = null, IReadOnlyList<string>? locales = null)
    {
        _associations = associations;
        _mime = mime;
        _launcher = launcher ?? new ProcessLauncher();
        _env = env ?? Environment.GetEnvironmentVariable;
        _locales = locales ?? [];
    }

    // A path or URL, opened with its default application.
    public void Open(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !uri.IsFile)
        {
            OpenWithDefault(target, "x-scheme-handler/" + uri.Scheme, [target]);
            return;
        }

        var path = Uri.TryCreate(target, UriKind.Absolute, out var fileUri) && fileUri.IsFile ? fileUri.LocalPath : target;
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Nothing to open.", path);

        var mime = _mime.GuessForFile(path);
        if (mime == "application/x-desktop")
        {
            Launch(DesktopEntry.Load(path, Path.GetFileName(path), _locales));
            return;
        }
        OpenWithDefault(path, mime, [path]);
    }

    public void OpenWith(DesktopEntry app, IReadOnlyList<string> targets) => Start(app, app.Exec, targets);

    // Runs the application (or one of its actions, e.g. "new-window") without any file.
    public void Launch(DesktopEntry app, string? actionId = null)
    {
        if (actionId is null)
        {
            Start(app, app.Exec, []);
            return;
        }
        var action = app.Actions.FirstOrDefault(a => a.Id == actionId)
            ?? throw new ArgumentException($"{app.Name} has no action '{actionId}'.", nameof(actionId));
        Start(app, action.Exec, []);
    }

    // The "Run" box: a command line, or failing that something Open can handle.
    public void Run(string commandLine)
    {
        var args = ExecLine.Tokenize(commandLine.Trim());
        if (args.Count == 0)
            throw new ArgumentException("Nothing to run.", nameof(commandLine));

        var program = FindExecutable(args[0]);
        if (program is not null)
            _launcher.Start(new ProcessRequest(program, args.Skip(1).ToList(), null));
        else
            Open(commandLine.Trim());
    }

    // True when TryExec (or the first word of Exec) can be found, so a launcher can hide dead entries.
    public bool IsAvailable(DesktopEntry app)
    {
        var program = app.TryExec ?? ExecLine.Tokenize(app.Exec ?? string.Empty).FirstOrDefault();
        return program is not null && FindExecutable(program) is not null;
    }

    public string? FindExecutable(string name)
    {
        if (name.Contains('/'))
            return IsExecutable(name) ? name : null;

        foreach (var dir in (_env("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (IsExecutable(candidate))
                return candidate;
        }
        return null;
    }

    private void OpenWithDefault(string target, string mime, IReadOnlyList<string> targets)
    {
        var app = _associations.GetDefault(mime) ?? throw new NoAssociationException(target, mime);
        OpenWith(app, targets);
    }

    private void Start(DesktopEntry app, string? exec, IReadOnlyList<string> targets)
    {
        if (exec is null)
            throw new InvalidOperationException($"{app.Name} has nothing to execute.");

        var argv = ExecLine.Expand(exec, targets, app.Name, app.Icon, app.FilePath).ToList();
        if (argv.Count == 0)
            throw new InvalidOperationException($"{app.Name} has an empty Exec line.");

        if (app.Terminal)
            argv = InTerminal(argv);

        var program = FindExecutable(argv[0]) ?? throw new FileNotFoundException($"Cannot find '{argv[0]}'.", argv[0]);
        _launcher.Start(new ProcessRequest(program, argv.Skip(1).ToList(), app.WorkingDirectory));
    }

    private List<string> InTerminal(List<string> argv)
    {
        var preferred = _env("TERMINAL");
        var candidates = Terminals.AsEnumerable();
        if (!string.IsNullOrEmpty(preferred))
            candidates = candidates.Prepend((preferred, Terminals.FirstOrDefault(t => t.Name == preferred).Prefix ?? ["-e"]));

        foreach (var (name, prefix) in candidates)
            if (FindExecutable(name) is { } terminal)
                return [terminal, .. prefix, .. argv];
        throw new FileNotFoundException("No terminal emulator found.");
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
