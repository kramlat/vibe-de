using System.Net.Sockets;

namespace Vde.DisplayManager;

public interface IGreeterConnection : IDisposable
{
    Task<GreeterResponse> SendAsync(GreeterRequest request, CancellationToken cancel = default);
}

// The greeter's end of the socket to the display manager.
public sealed class GreeterConnection : IGreeterConnection
{
    private readonly NetworkStream _stream;

    private GreeterConnection(NetworkStream stream) => _stream = stream;

    public static GreeterConnection Connect(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        return new GreeterConnection(new NetworkStream(socket, ownsSocket: true));
    }

    public static string? SocketFromEnvironment(Func<string, string?> env) => env("VDEDM_SOCK") ?? env("GREETD_SOCK");

    public async Task<GreeterResponse> SendAsync(GreeterRequest request, CancellationToken cancel = default)
    {
        await _stream.WriteAsync(Greetd.Encode(request), cancel);
        var frame = await Greetd.ReadFrameAsync(_stream, cancel) ?? throw new IOException("the display manager closed the connection");
        return Greetd.DecodeResponse(frame);
    }

    public void Dispose() => _stream.Dispose();
}

public enum GreeterStage { ChooseUser, Prompt, Working, Done }

// What the login screen is doing, independent of how it looks: which user, which question is being asked, what
// message to show, and what happens with each answer. The window only displays this and forwards input.
public sealed class GreeterModel
{
    private readonly IGreeterConnection _connection;
    private string? _user;

    public GreeterModel(IGreeterConnection connection, IReadOnlyList<LoginUser> users, IReadOnlyList<SessionEntry> sessions, string? defaultSession = null)
    {
        _connection = connection;
        Users = users;
        Sessions = sessions.Where(s => s.Kind == SessionKind.Wayland).ToList();   // X11 sessions cannot be started yet
        Session = SessionDirectory.Choose(Sessions, defaultSession);
    }

    public IReadOnlyList<LoginUser> Users { get; }
    public IReadOnlyList<SessionEntry> Sessions { get; }
    public SessionEntry? Session { get; set; }
    public GreeterStage Stage { get; private set; } = GreeterStage.ChooseUser;
    public string? UserName => _user;
    public string? PromptText { get; private set; }
    public bool PromptSecret { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public event Action? Changed;
    // The login worked and the session was handed to the display manager: the greeter can go.
    public event Action? SessionStarted;

    // Starts a login for this user: the first question (usually the password) comes next.
    public async Task BeginAsync(string username, CancellationToken cancel = default)
    {
        _user = username;
        Message = null;
        Stage = GreeterStage.Working;
        Changed?.Invoke();
        await Handle(await SendSafe(new CreateSession(username), cancel), cancel);
    }

    // The answer to the current question.
    public async Task AnswerAsync(string answer, CancellationToken cancel = default)
    {
        if (Stage != GreeterStage.Prompt) return;
        Stage = GreeterStage.Working;
        PromptText = null;
        Changed?.Invoke();
        await Handle(await SendSafe(new PostAuthMessageResponse(answer), cancel), cancel);
    }

    // Back to choosing a user.
    public async Task CancelAsync(CancellationToken cancel = default)
    {
        await SendSafe(new CancelSession(), cancel);
        _user = null;
        PromptText = null;
        Message = null;
        Stage = GreeterStage.ChooseUser;
        Changed?.Invoke();
    }

    private async Task<GreeterResponse> SendSafe(GreeterRequest request, CancellationToken cancel)
    {
        try { return await _connection.SendAsync(request, cancel); }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
        {
            return new ErrorResponse(ErrorType.Error, "Lost contact with the login service. " + e.Message);
        }
    }

    private async Task Handle(GreeterResponse response, CancellationToken cancel)
    {
        switch (response)
        {
            case AuthMessage { Type: AuthMessageType.Secret or AuthMessageType.Visible } question:
                PromptText = question.Text.TrimEnd().TrimEnd(':').Trim();
                PromptSecret = question.Type == AuthMessageType.Secret;
                Stage = GreeterStage.Prompt;
                Changed?.Invoke();
                return;
            case AuthMessage notice:   // something to read (a fingerprint reader asking you to touch it): show it and carry on
                Message = notice.Text;
                MessageIsError = notice.Type == AuthMessageType.Error;
                Changed?.Invoke();
                await Handle(await SendSafe(new PostAuthMessageResponse(null), cancel), cancel);
                return;
            case Success when Session is not null:
                await StartAsync(cancel);
                return;
            case Success:
                await Fail("There is no desktop session installed to start.", cancel);
                return;
            case ErrorResponse { Type: ErrorType.AuthError }:
                // the way a lock screen behaves: say so, and ask again for the same person
                var again = _user;
                Message = "That password is not right. Try again.";
                MessageIsError = true;
                if (again is not null) await RetryAsync(again, cancel);
                return;
            case ErrorResponse error:
                await Fail(error.Description, cancel);
                return;
        }
    }

    private async Task RetryAsync(string user, CancellationToken cancel)
    {
        var keep = Message;
        Stage = GreeterStage.Working;
        Changed?.Invoke();
        var response = await SendSafe(new CreateSession(user), cancel);
        await Handle(response, cancel);
        if (Stage == GreeterStage.Prompt) { Message = keep; MessageIsError = true; Changed?.Invoke(); }
    }

    private async Task StartAsync(CancellationToken cancel)
    {
        var response = await SendSafe(new StartSession(Session!.Command, []), cancel);
        if (response is Success)
        {
            Stage = GreeterStage.Done;
            Changed?.Invoke();
            SessionStarted?.Invoke();
        }
        else await Fail(response is ErrorResponse e ? e.Description : "The session could not be started.", cancel);
    }

    private Task Fail(string message, CancellationToken cancel)
    {
        Message = message;
        MessageIsError = true;
        _user = null;
        Stage = GreeterStage.ChooseUser;
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
