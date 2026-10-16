using System.Diagnostics;
using Vibe.Registry;

namespace Vde.Shell;

public sealed record ExecResult(int ExitCode, string StandardError, bool StillRunning);

// Runs a program. Split out so SudoBackend's logic can be tested without a real sudo.
public interface IProcessExecutor
{
    // Starts it, writes standardInput (if any) and waits up to wait for it to finish.
    // A program still running after that is reported as StillRunning and left to run.
    Task<ExecResult> RunAsync(IReadOnlyList<string> commandLine, string? standardInput, TimeSpan wait, CancellationToken cancel);
}

public sealed class ProcessExecutor : IProcessExecutor
{
    public async Task<ExecResult> RunAsync(IReadOnlyList<string> commandLine, string? standardInput, TimeSpan wait, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(commandLine[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in commandLine.Skip(1))
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {commandLine[0]}.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(wait);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new ExecResult(0, string.Empty, StillRunning: true);   // it is a long-running program: leave it be
        }
        return new ExecResult(process.ExitCode, await process.StandardError.ReadToEndAsync(cancel), StillRunning: false);
    }
}

// Elevation through sudo. The password goes to sudo on its standard input and is never part of a command
// line. It is checked first (sudo -v), the command is then run on that authentication, and the cached
// authentication is dropped again straight away so nothing stays elevated afterwards.
public sealed class SudoBackend : IElevationBackend
{
    private static readonly string[] AdminGroups = ["wheel", "sudo", "admin"];

    private readonly IProcessExecutor _executor;
    private readonly string _groupFile;
    private readonly string _sudo;

    private readonly string _passwdFile;
    private readonly bool _rootPassword;

    // rootPassword: sudo is configured with rootpw, so it asks for root's password instead of the user's.
    public SudoBackend(IProcessExecutor? executor = null, string groupFile = "/etc/group", string sudo = "sudo",
        string passwdFile = "/etc/passwd", bool rootPassword = false)
    {
        _executor = executor ?? new ProcessExecutor();
        _groupFile = groupFile;
        _sudo = sudo;
        _passwdFile = passwdFile;
        _rootPassword = rootPassword;
    }

    // By default sudo asks for the invoking user's own password; with rootpw it asks for root's.
    public string? PasswordAccount(RegistryIdentity identity)
    {
        if (_rootPassword)
            return "root";
        if (File.Exists(_passwdFile))
            foreach (var line in File.ReadLines(_passwdFile))
            {
                var fields = line.Split(':');   // name:x:uid:gid:...
                if (fields.Length >= 3 && fields[0].Length > 0 && uint.TryParse(fields[2], out var uid) && uid == identity.Uid)
                    return fields[0];
            }
        return null;
    }

    // Root needs no elevation; members of the usual administrator groups may; so may anyone sudo lets in without
    // a password. Rules in sudoers that name a single user are not visible from here: such a user is rejected.
    public bool CanElevate(RegistryIdentity identity)
    {
        if (identity.IsRoot)
            return true;
        if (AdminGroupIds().Any(identity.InGroup))
            return true;

        try
        {
            return _executor.RunAsync([_sudo, "-n", "true"], null, TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult().ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;   // no sudo installed
        }
    }

    public async Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel)
    {
        // 1. check the password; -k ignores any authentication cached earlier, so it is always really asked for
        var check = await _executor.RunAsync(
            [_sudo, "-k", "-S", "-p", "", "-v"], new string(password.Span) + "\n", TimeSpan.FromSeconds(15), cancel);
        if (check.ExitCode != 0)
            return Classify(check.StandardError);

        // 2. run the command on that authentication, without asking again
        var run = await _executor.RunAsync([_sudo, "-n", "--", .. commandLine], null, TimeSpan.FromMilliseconds(600), cancel);

        // 3. forget the authentication whatever happened
        await _executor.RunAsync([_sudo, "-k"], null, TimeSpan.FromSeconds(5), CancellationToken.None);

        if (!run.StillRunning && run.ExitCode != 0)
            return Classify(run.StandardError, fallback: ElevationStatus.Failed);
        return ElevationOutcome.Success;
    }

    private static ElevationOutcome Classify(string standardError, ElevationStatus fallback = ElevationStatus.WrongPassword)
    {
        var message = standardError.Trim();
        if (message.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not allowed to execute", StringComparison.OrdinalIgnoreCase))
            return new ElevationOutcome(ElevationStatus.NotAllowed, message);
        if (message.Contains("try again", StringComparison.OrdinalIgnoreCase)
            || message.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("a password is required", StringComparison.OrdinalIgnoreCase))
            return new ElevationOutcome(ElevationStatus.WrongPassword, message);
        return new ElevationOutcome(fallback, message.Length == 0 ? null : message);
    }

    private IEnumerable<uint> AdminGroupIds()
    {
        if (!File.Exists(_groupFile))
            yield break;
        foreach (var line in File.ReadLines(_groupFile))
        {
            var fields = line.Split(':');   // name:password:gid:members
            if (fields.Length >= 3 && AdminGroups.Contains(fields[0]) && uint.TryParse(fields[2], out var gid))
                yield return gid;
        }
    }
}
