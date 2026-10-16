using Vibe.Registry;

namespace Vde.Shell;

// Running an untrusted shell action with more rights, with the user's explicit consent (a UAC-like layer):
//   - if the user is not allowed to elevate, the action is rejected;
//   - otherwise the prompt shows the exact command line about to run and asks for the password, with Cancel.

public sealed record ElevationRequest(
    string Label,                          // "Edit with Thing"
    string Verb,
    string Path,                           // the file the action is for
    IReadOnlyList<string> CommandLine,     // program and arguments, exactly as they will be run
    string Reason)                         // why it needed elevation: "it is not executable by you (mode 0644)"
{
    // Whose password sudo is waiting for (the user's own, or root's when sudo is set up with rootpw); shown in the prompt.
    public string? Account { get; init; }

    // The command line for display, quoted so that every argument is unambiguous.
    public string CommandText => string.Join(' ', CommandLine.Select(Quote));

    private static string Quote(string argument)
        => argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./=:+,@%".Contains(c))
            ? argument
            : "'" + argument.Replace("'", "'\\''") + "'";
}

// The password is a char array so it can be wiped once it has been used.
public sealed record ElevationResponse(bool Approved, char[]? Password)
{
    public static readonly ElevationResponse Cancel = new(false, null);
}

// Implemented by the UI (a dialog). attempt starts at 1; previousError is set when the last password was wrong.
public interface IElevationPrompt
{
    Task<ElevationResponse> AskAsync(ElevationRequest request, int attempt, string? previousError, CancellationToken cancel);
}

public enum ElevationStatus
{
    Success,
    WrongPassword,
    NotAllowed,    // authenticated, but the policy does not let this user run it
    Failed,        // anything else: the command could not be started
}

public sealed record ElevationOutcome(ElevationStatus Status, string? Message = null)
{
    public static readonly ElevationOutcome Success = new(ElevationStatus.Success);
}

public interface IElevationBackend
{
    // A quick check that does not ask for a password, so a user who cannot elevate is rejected before any prompt.
    bool CanElevate(RegistryIdentity identity);

    // The account whose password the prompt must ask for, or null when it is just "your password".
    string? PasswordAccount(RegistryIdentity identity) => null;

    Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel);
}

// Pairs the pieces ShellExecute needs. Without it, untrusted actions are simply rejected.
public sealed record ElevationOptions(IElevationBackend Backend, IElevationPrompt Prompt, RegistryIdentity Identity)
{
    public const int MaxAttempts = 3;
}

public sealed class ElevationDeniedException(string message) : Exception(message);

// How this machine's sudo is set up, as the administrator recorded it in the system hive:
// HKLM\Software\Vde\Elevation, PasswordFor = "user" (the default) or "root" (sudo uses rootpw).
public static class ElevationSettings
{
    public const string KeyPath = @"Software\Vde\Elevation";

    public static bool AsksForRootPassword(IHive? system)
        => system?.GetValue(KeyPath, "PasswordFor") is { Kind: RegistryValueKind.String, Data: string s } && s.Equals("root", StringComparison.OrdinalIgnoreCase);
}
