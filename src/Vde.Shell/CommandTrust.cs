using Vibe.Registry;

namespace Vde.Shell;

public sealed class UntrustedCommandException(string verb, string reason, string? path = null, string? command = null)
    : Exception($"The '{verb}' command is not trusted: {reason}")
{
    public string Verb { get; } = verb;
    public string Reason { get; } = reason;
    public string? Path { get; } = path;          // the file the action was for
    public string? Command { get; } = command;    // the registry command line, before placeholders are filled in
}

// Whether the shell may run a command stored in the registry. The value holding the command line needs the
// same things a program file needs on Unix:
//   - the caller may execute it (the x bit that applies to them),
//   - it belongs to the caller or to root, so another user cannot plant a command that runs as you,
//   - nobody else can write to it.
// Values created by an import or a plain write are 0644 and so inert until someone marks them executable.
// A command owned by root with mode 0700 can only be run by root: a root-only action.
public sealed record CommandTrust(bool IsTrusted, string? Reason)
{
    public static readonly CommandTrust Trusted = new(true, null);

    public static CommandTrust Evaluate(PosixPermissions? permissions, RegistryIdentity identity)
    {
        if (permissions is not { } p)
            return new(false, "it has no permissions");
        if (!identity.Allows(p, Access.Execute) || (identity.IsRoot && (p.Mode & 0x49) == 0))   // root needs some x bit too
            return new(false, $"it is not executable by you (mode {p.ModeText})");
        if (p.Uid != 0 && p.Uid != identity.Uid)
            return new(false, $"it belongs to another user (uid {p.Uid})");
        if ((p.Mode & 0x2) != 0)
            return new(false, "anyone can write to it");
        return Trusted;
    }
}
