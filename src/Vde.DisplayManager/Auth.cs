namespace Vde.DisplayManager;

// What happens while someone logs in, as the greeter needs to show it.
public abstract record AuthEvent;
public sealed record Prompt(bool Secret, string Text) : AuthEvent;     // a question: answer with Respond
public sealed record Notice(bool IsError, string Text) : AuthEvent;    // something to read; acknowledge with Respond(null)
public sealed record Authenticated : AuthEvent;
public sealed record Failed(string Reason, bool IsAuthError) : AuthEvent;

// How the session being opened presents itself to the system (logind).
public sealed record SessionInfo(string? Tty, int Vt, string Seat = "seat0", string Type = "wayland", string Class = "user", string? Desktop = null);

// One person's login attempt. Implemented over PAM (PamAuthenticator); tests use a fake.
public interface IAuthSession : IDisposable
{
    // The next thing to show or the outcome. Completes when there is one.
    Task<AuthEvent> NextAsync(CancellationToken cancel = default);
    // The answer to the last Prompt (or null to acknowledge a Notice).
    void Respond(string? answer);
    // After Authenticated: set up the session (credentials, logind) and return the environment it should have.
    IReadOnlyDictionary<string, string> OpenSession(SessionInfo info);
    // Ends the session or abandons the attempt. Safe to call any time, and more than once.
    void Close();
}

public interface IAuthenticator
{
    IAuthSession Begin(string username);
}
