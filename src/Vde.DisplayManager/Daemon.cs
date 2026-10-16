using System.Net.Sockets;

namespace Vde.DisplayManager;

public interface IManagedProcess
{
    Task<int> Exited { get; }
    void Terminate();
    void Kill();
}

// What the daemon needs from the machine, so it can be run against fakes: virtual terminals, ownership of
// terminals, starting processes as users, and the account database.
public interface IDmSystem
{
    void SwitchVt(int vt);
    void GiveTty(string tty, int uid, int gid);
    IManagedProcess Start(LaunchPlan plan);
    LoginUser? FindUser(string name);
    string Launcher { get; }   // the vterm-launch helper
}

// The display manager: shows the greeter, authenticates through it, starts the chosen desktop as the user, and
// shows the greeter again when the desktop ends. One of these runs for the whole life of the machine's seat.
public sealed class Daemon
{
    private readonly DmConfig _config;
    private readonly IAuthenticator _login, _greeterPam, _autologinPam;
    private readonly IDmSystem _system;
    private readonly Func<IReadOnlyList<SessionEntry>> _sessions;
    private readonly TextWriter _log;

    public Daemon(DmConfig config, IAuthenticator login, IAuthenticator greeterPam, IAuthenticator autologinPam, IDmSystem system,
        Func<IReadOnlyList<SessionEntry>> sessions, TextWriter log)
    {
        _config = config;
        _login = login;
        _greeterPam = greeterPam;
        _autologinPam = autologinPam;
        _system = system;
        _sessions = sessions;
        _log = log;
    }

    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan GreeterExitWait { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FailureDelay { get; init; } = TimeSpan.FromSeconds(1);
    // Raised each time the greeter is up and listening (for tests and for status displays).
    public event Action? GreeterReady;
    public event Action<string>? SessionStarted;
    public event Action<string, int>? SessionEnded;

    public async Task RunAsync(CancellationToken stop)
    {
        var first = true;
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            PendingSession? pending = null;
            if (_config.AutoLoginUser is { } who && (first || !_config.AutoLoginOnce))
                pending = await AutologinAsync(who);
            first = false;
            pending ??= await GreeterAsync(stop);
            if (pending is null)
            {
                failures++;
                try { await Task.Delay(TimeSpan.FromTicks(RestartDelay.Ticks * Math.Min(failures, 10)), stop); } catch (OperationCanceledException) { }
                continue;
            }
            failures = 0;
            await SessionAsync(pending, stop);
        }
    }

    private async Task<PendingSession?> AutologinAsync(string username)
    {
        try
        {
            var user = _system.FindUser(username);
            if (user is null) { _log.WriteLine($"vdedm: autologin user '{username}' does not exist"); return null; }
            var sessions = _sessions();
            var entry = SessionDirectory.Choose(sessions, _config.AutoLoginSession ?? _config.DefaultSession);
            if (entry is null) { _log.WriteLine("vdedm: autologin: there is no session to start"); return null; }
            var auth = _autologinPam.Begin(username);
            if (await auth.NextAsync() is not Authenticated)
            {
                _log.WriteLine($"vdedm: autologin of '{username}' was refused by PAM (service {_config.AutologinPamService})");
                auth.Close();
                return null;
            }
            _log.WriteLine($"vdedm: logging in {username} automatically");
            return new PendingSession(user, entry.Command, [], auth);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _log.WriteLine("vdedm: autologin failed: " + e.Message);
            return null;
        }
    }

    // Shows the greeter. Returns the login it produced, or null if the greeter ended without one (or we are stopping).
    private async Task<PendingSession?> GreeterAsync(CancellationToken stop)
    {
        var greeterUser = _system.FindUser(_config.GreeterUser);
        if (greeterUser is null) { _log.WriteLine($"vdedm: the greeter account '{_config.GreeterUser}' does not exist"); return null; }
        IAuthSession? pam = null;
        IManagedProcess? greeter = null;
        Socket? listener = null;
        var manager = new LoginManager(_login, _system.FindUser) { FailureDelay = FailureDelay };
        try
        {
            pam = _greeterPam.Begin(_config.GreeterUser);
            if (await pam.NextAsync(stop) is not Authenticated) { _log.WriteLine("vdedm: PAM refused the greeter session"); return null; }
            var tty = $"/dev/tty{_config.Vt}";
            var pamEnv = pam.OpenSession(new SessionInfo(tty, _config.Vt, Class: "greeter", Desktop: "VDE"));

            listener = Listen(greeterUser);
            _system.SwitchVt(_config.Vt);
            _system.GiveTty(tty, greeterUser.Uid, greeterUser.Gid);
            greeter = _system.Start(SessionPlans.Greeter(_config.GreeterUser, _config.GreeterHome, _config.GreeterCommand, _config.SocketPath, _config.Vt, _system.Launcher, pamEnv));
            _log.WriteLine("vdedm: greeter started");
            GreeterReady?.Invoke();

            while (!stop.IsCancellationRequested)
            {
                var accept = listener.AcceptAsync(stop).AsTask();
                if (await Task.WhenAny(accept, greeter.Exited) != accept) { _log.WriteLine("vdedm: the greeter ended without logging anyone in"); return null; }
                using var connection = await accept;
                await using var stream = new NetworkStream(connection, ownsSocket: false);
                if (await ServeAsync(stream, manager, greeter, stop) is { } pending)
                {
                    await StopGreeterAsync(greeter);
                    return pending;
                }
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
        {
            _log.WriteLine("vdedm: greeter failed: " + e.Message);
            return null;
        }
        finally
        {
            manager.Abandon();
            if (greeter is not null) await StopGreeterAsync(greeter);
            listener?.Dispose();
            try { File.Delete(_config.SocketPath); } catch (IOException) { }
            pam?.Close();
        }
    }

    // One greeter connection. Returns the login when the greeter started a session.
    private async Task<PendingSession?> ServeAsync(Stream stream, LoginManager manager, IManagedProcess greeter, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            byte[]? frame;
            try { frame = await Greetd.ReadFrameAsync(stream, stop); }
            catch (InvalidDataException e) { _log.WriteLine("vdedm: bad message from the greeter: " + e.Message); return null; }
            if (frame is null) return null;
            GreeterResponse response;
            try { response = await manager.HandleAsync(Greetd.DecodeRequest(frame), stop); }
            catch (InvalidDataException e) { response = new ErrorResponse(ErrorType.Error, e.Message); }
            await stream.WriteAsync(Greetd.Encode(response), stop);
            if (manager.Pending is { } pending && response is Success) return pending;
        }
        return null;
    }

    private async Task StopGreeterAsync(IManagedProcess greeter)
    {
        if (greeter.Exited.IsCompleted) return;
        greeter.Terminate();
        if (await Task.WhenAny(greeter.Exited, Task.Delay(GreeterExitWait)) != greeter.Exited)
        {
            greeter.Kill();
            await greeter.Exited;
        }
    }

    private Socket Listen(LoginUser greeterUser)
    {
        var dir = Path.GetDirectoryName(_config.SocketPath)!;
        Directory.CreateDirectory(dir);
        try { File.Delete(_config.SocketPath); } catch (IOException) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_config.SocketPath));
        try { _system.GiveTty(_config.SocketPath, greeterUser.Uid, greeterUser.Gid); }   // only the greeter's account may talk to us
        catch (IOException) { File.SetUnixFileMode(_config.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        listener.Listen(1);
        return listener;
    }

    private async Task SessionAsync(PendingSession pending, CancellationToken stop)
    {
        var tty = $"/dev/tty{_config.Vt}";
        IManagedProcess? process = null;
        try
        {
            var entry = _sessions().FirstOrDefault(s => s.Command.SequenceEqual(pending.Command));
            _system.SwitchVt(_config.Vt);
            _system.GiveTty(tty, pending.User.Uid, pending.User.Gid);
            var pamEnv = pending.Auth.OpenSession(new SessionInfo(tty, _config.Vt, Desktop: entry?.DesktopNames));
            var plan = SessionPlans.Session(pending, pamEnv, _config.Vt, _system.Launcher, entry?.DesktopNames);
            process = _system.Start(plan);
            _log.WriteLine($"vdedm: session of {pending.User.Name} started");
            SessionStarted?.Invoke(pending.User.Name);
            var ended = await Task.WhenAny(process.Exited, Task.Delay(Timeout.Infinite, stop));
            if (ended != process.Exited)
            {
                process.Terminate();   // shutting down: ask the desktop to end
                if (await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(10))) != process.Exited) process.Kill();
            }
            var code = await process.Exited;
            _log.WriteLine($"vdedm: session of {pending.User.Name} ended ({code})");
            SessionEnded?.Invoke(pending.User.Name, code);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.WriteLine("vdedm: the session could not be started: " + e.Message);
        }
        finally
        {
            pending.Auth.Close();   // closes the PAM session: logind ends the user's session
            try { _system.GiveTty(tty, 0, 5); } catch (IOException) { }   // the terminal goes back to root (group tty)
        }
    }
}
