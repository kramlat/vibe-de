namespace Vde.Shell;

public sealed class UntrustedCommandException(string verb, string reason, string? path = null, string? command = null)
    : Exception($"The '{verb}' command is not trusted: {reason}")
{
    public string Verb { get; } = verb;
    public string Reason { get; } = reason;
    public string? Path { get; } = path;          // the file the action was for
    public string? Command { get; } = command;    // the registry command line, before placeholders are filled in
}
