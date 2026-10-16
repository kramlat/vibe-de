namespace Vde.DisplayManager;

// A login that is ready to become a desktop: the greeter said start_session after a successful authentication.
public sealed record PendingSession(LoginUser User, IReadOnlyList<string> Command, IReadOnlyList<string> Environment, IAuthSession Auth);

// One greeter connection's side of the conversation: what each request does to the login in progress. Everything
// the system does (PAM, looking up the account) is passed in, so the rules here can be tested without any of it.
public sealed class LoginManager
{
    private enum Stage { Idle, Authenticating, Authenticated, Started }

    private readonly IAuthenticator _authenticator;
    private readonly Func<string, LoginUser?> _lookup;
    private IAuthSession? _auth;
    private LoginUser? _user;
    private string? _username;
    private Stage _stage;
    private int _failures;

    public LoginManager(IAuthenticator authenticator, Func<string, LoginUser?> lookup)
    {
        _authenticator = authenticator;
        _lookup = lookup;
    }

    // After a failed attempt the next one waits (doubling, up to Max): a script guessing passwords is slowed down
    // even when the PAM modules do not do it themselves.
    public TimeSpan FailureDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxFailureDelay { get; init; } = TimeSpan.FromSeconds(16);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, c) => Task.Delay(t, c);

    // Set when start_session succeeded. The daemon starts it once the greeter has gone.
    public PendingSession? Pending { get; private set; }

    public async Task<GreeterResponse> HandleAsync(GreeterRequest request, CancellationToken cancel = default)
    {
        switch (request)
        {
            case CreateSession create: return await CreateAsync(create.Username, cancel);
            case PostAuthMessageResponse reply:
                if (_stage != Stage.Authenticating || _auth is null) return new ErrorResponse(ErrorType.Error, "no authentication is waiting for an answer");
                _auth.Respond(reply.Response);
                return await NextAsync(cancel);
            case StartSession start: return Start(start);
            case CancelSession:
                Reset();
                return new Success();
            default: return new ErrorResponse(ErrorType.Error, "unknown request");
        }
    }

    // The greeter went away (or the daemon is shutting down): abandon whatever was in progress.
    public void Abandon() => Reset();

    private async Task<GreeterResponse> CreateAsync(string username, CancellationToken cancel)
    {
        Reset();
        if (username.Length is 0 or > 256 || username.Any(c => char.IsControl(c) || c == ':' || char.IsWhiteSpace(c)))
            return new ErrorResponse(ErrorType.Error, "that is not a valid user name");
        if (_failures > 0)
            await Delay(TimeSpan.FromTicks(Math.Min(FailureDelay.Ticks << Math.Min(_failures - 1, 10), MaxFailureDelay.Ticks)), cancel);
        _username = username;
        _user = _lookup(username);   // a user PAM knows but /etc/passwd does not (LDAP, homed) is looked up again at start_session
        _auth = _authenticator.Begin(username);
        _stage = Stage.Authenticating;
        return await NextAsync(cancel);
    }

    private async Task<GreeterResponse> NextAsync(CancellationToken cancel)
    {
        var auth = _auth!;
        AuthEvent ev;
        try { ev = await auth.NextAsync(cancel); }
        catch (Exception e) when (e is System.Threading.Channels.ChannelClosedException or InvalidOperationException)
        {
            Reset();
            return new ErrorResponse(ErrorType.Error, "authentication ended unexpectedly");
        }
        switch (ev)
        {
            case Prompt p: return new AuthMessage(p.Secret ? AuthMessageType.Secret : AuthMessageType.Visible, p.Text);
            case Notice n: return new AuthMessage(n.IsError ? AuthMessageType.Error : AuthMessageType.Info, n.Text);
            case Authenticated:
                _stage = Stage.Authenticated;
                _failures = 0;
                return new Success();
            case Failed f:
                _failures++;
                Reset();
                return new ErrorResponse(f.IsAuthError ? ErrorType.AuthError : ErrorType.Error, f.Reason);
            default: return new ErrorResponse(ErrorType.Error, "unexpected authentication state");
        }
    }

    private GreeterResponse Start(StartSession start)
    {
        if (_stage != Stage.Authenticated || _auth is null) return new ErrorResponse(ErrorType.Error, "log in first");
        if (start.Cmd.Count == 0 || start.Cmd[0].Length == 0 || start.Cmd.Any(c => c.Contains('\0')))
            return new ErrorResponse(ErrorType.Error, "no session command given");
        foreach (var entry in start.Env)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || !ValidEnvName(entry[..eq]))
                return new ErrorResponse(ErrorType.Error, $"'{entry}' is not a NAME=value environment entry");
        }
        var user = _user ?? (_username is null ? null : _lookup(_username));
        if (user is null) return new ErrorResponse(ErrorType.Error, "the account does not exist on this computer");
        Pending = new PendingSession(user, start.Cmd, start.Env, _auth);
        _stage = Stage.Started;
        _auth = null;   // the pending session owns it now
        return new Success();
    }

    private static bool ValidEnvName(string name)
        => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    // Abandons the attempt in progress. The failure count stays: cancelling is not a way to reset it.
    private void Reset()
    {
        _auth?.Close();
        _auth = null;
        _user = null;
        _username = null;
        _stage = Stage.Idle;
    }
}
