using Vde.DisplayManager;

namespace Vde.DisplayManager.Tests;

// These run the real libpam with a service file of their own (pam_start_confdir), so they need no root and touch
// nothing in /etc/pam.d.
public class PamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vde-pam-" + Guid.NewGuid().ToString("N"));
    private static readonly string User = Environment.UserName;

    public PamTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PamAuthenticator Service(string name, string conf)
    {
        File.WriteAllText(Path.Combine(_dir, name), conf);
        return new PamAuthenticator(name, _dir);
    }

    private string Script(string body)
    {
        var path = Path.Combine(_dir, "check.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<AuthEvent> Next(IAuthSession s) => await s.NextAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

    [Fact]
    public async Task A_permissive_service_authenticates_and_the_session_opens_and_closes()
    {
        var pam = Service("permit", "auth required pam_permit.so\naccount required pam_permit.so\nsession required pam_permit.so\n");
        using var session = pam.Begin(User);

        Assert.IsType<Authenticated>(await Next(session));
        var env = session.OpenSession(new SessionInfo("/dev/tty7", 7, Desktop: "VDE"));

        Assert.Equal("VDE", env["XDG_CURRENT_DESKTOP"]);   // what we put in for pam_systemd comes back in the environment
        Assert.Equal("7", env["XDG_VTNR"]);
        Assert.Equal("wayland", env["XDG_SESSION_TYPE"]);
        session.Close();
        session.Close();   // twice is fine
    }

    [Fact]
    public async Task A_denying_service_reports_an_authentication_failure()
    {
        var pam = Service("deny", "auth required pam_deny.so\naccount required pam_permit.so\n");
        using var session = pam.Begin(User);

        var failed = Assert.IsType<Failed>(await Next(session));
        Assert.True(failed.IsAuthError);
    }

    [Fact]
    public async Task The_password_is_asked_for_through_the_conversation_and_checked_by_the_module()
    {
        var check = Script("read pw\n[ \"$pw\" = secret ]");
        var pam = Service("password", $"auth required pam_exec.so expose_authtok quiet {check}\naccount required pam_permit.so\n");

        using (var good = pam.Begin(User))
        {
            var prompt = Assert.IsType<Prompt>(await Next(good));
            Assert.True(prompt.Secret);
            Assert.Contains("Password", prompt.Text);
            good.Respond("secret");
            Assert.IsType<Authenticated>(await Next(good));
        }
        using (var bad = pam.Begin(User))
        {
            Assert.IsType<Prompt>(await Next(bad));
            bad.Respond("wrong");
            Assert.True(Assert.IsType<Failed>(await Next(bad)).IsAuthError);
        }
    }

    [Fact]
    public async Task Abandoning_a_login_that_waits_for_a_password_does_not_hang()
    {
        var check = Script("read pw\n[ \"$pw\" = secret ]");
        var pam = Service("password", $"auth required pam_exec.so expose_authtok quiet {check}\naccount required pam_permit.so\n");
        var session = pam.Begin(User);
        Assert.IsType<Prompt>(await Next(session));

        session.Close();

        // the attempt ends: no Authenticated, and the PAM thread is released
        var end = await Next(session);
        Assert.IsNotType<Authenticated>(end);
    }

    [Fact]
    public async Task A_service_that_does_not_exist_fails_cleanly()
    {
        using var session = new PamAuthenticator("no-such-service", _dir).Begin(User);
        // pam falls back to the "other" service or fails; either way the greeter gets an answer rather than silence
        var result = await Next(session);
        Assert.True(result is Failed or Authenticated or Prompt);
        if (result is Prompt) session.Close();
    }
}
