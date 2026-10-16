using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vde.DisplayManager;
using VdedmGreeter;

namespace VdedmGreeter.Tests;

public class GreeterWindowTests
{
    private sealed class Fake(Func<GreeterRequest, GreeterResponse> reply) : IGreeterConnection
    {
        public List<GreeterRequest> Sent { get; } = [];
        public Task<GreeterResponse> SendAsync(GreeterRequest request, CancellationToken cancel = default) { Sent.Add(request); return Task.FromResult(reply(request)); }
        public void Dispose() { }
    }

    private static readonly LoginUser Alice = new("alice", 1000, 1000, "/home/alice", "/bin/fish", "Alice Anders");
    private static readonly LoginUser Bob = new("bob", 1001, 1001, "/home/bob", "/bin/bash", "Bob");
    private static readonly SessionEntry Vde = new("vde", "Vibe Desktop Environment", "", ["start-vde"], SessionKind.Wayland, "VDE");
    private static readonly SessionEntry Plasma = new("plasma", "Plasma", "", ["startplasma-wayland"], SessionKind.Wayland, "KDE");

    private static GreeterWindow Make(Fake connection, IReadOnlyList<LoginUser>? users = null, Action<string>? power = null)
    {
        var model = new GreeterModel(connection, users ?? [Alice, Bob], [Vde, Plasma]);
        var window = new GreeterWindow(model, now: () => new DateTime(2026, 10, 16, 21, 5, 0), power: power) { Width = 1000, Height = 640, WindowState = WindowState.Normal, SystemDecorations = SystemDecorations.Full };
        window.Show();
        return window;
    }

    private static IEnumerable<Button> Buttons(GreeterWindow w) => w.GetVisualDescendants().OfType<Button>();

    [AvaloniaFact]
    public async Task Several_users_are_shown_as_tiles_and_picking_one_asks_for_the_password_which_logs_in()
    {
        var connection = new Fake(r => r switch
        {
            CreateSession => new AuthMessage(AuthMessageType.Secret, "Password: "),
            _ => new Success(),
        });
        var window = Make(connection);
        Assert.Equal(new[] { "alice", "bob" }, Buttons(window).Select(b => b.Tag as string).Where(t => t is not null));
        Assert.Contains("05", window.ClockText);   // the clock shows the time it was given (21:05; 9:05 PM in some cultures)

        await window.Model.BeginAsync("alice");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(GreeterStage.Prompt, window.Model.Stage);
        Assert.True(window.PasswordBox.IsVisible);
        Assert.Equal('•', window.PasswordBox.PasswordChar);

        var started = false;
        window.Model.SessionStarted += () => started = true;
        window.PasswordBox.Text = "pw";
        await window.Submit();

        Assert.True(started);
        Assert.Equal(string.Empty, window.PasswordBox.Text);   // the password does not stay in the box
        Assert.Equal(new[] { "start-vde" }, connection.Sent.OfType<StartSession>().Single().Cmd);
    }

    [AvaloniaFact]
    public async Task A_wrong_password_shows_the_message_and_the_password_box_again()
    {
        var n = 0;
        var window = Make(new Fake(r => r switch
        {
            CreateSession => new AuthMessage(AuthMessageType.Secret, "Password: "),
            PostAuthMessageResponse => ++n == 1 ? new ErrorResponse(ErrorType.AuthError, "x") : new Success(),
            _ => new Success(),
        }), [Alice]);
        await window.Model.BeginAsync("alice");
        window.PasswordBox.Text = "bad";

        await window.Submit();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("not right", window.MessageText);
        Assert.Equal(GreeterStage.Prompt, window.Model.Stage);
    }

    [AvaloniaFact]
    public void A_single_user_machine_goes_straight_to_the_password_and_the_power_buttons_call_the_host()
    {
        var powered = new List<string>();
        var window = Make(new Fake(r => r is CreateSession ? new AuthMessage(AuthMessageType.Secret, "Password: ") : new Success()), [Alice], powered.Add);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Task.Delay(100).Wait();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("alice", window.Model.UserName);

        foreach (var b in Buttons(window).Where(b => b.Content is Vde.Shell.Controls.ShellIcon))
            b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "reboot", "poweroff" }, powered);
    }

    [AvaloniaFact]
    public async Task Pictures()
    {
        if (Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR") is not { Length: > 0 } dir) return;
        var window = Make(new Fake(r => r is CreateSession ? new AuthMessage(AuthMessageType.Secret, "Password: ") : new ErrorResponse(ErrorType.AuthError, "no")));
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "greeter-users.png"));
        await window.Model.BeginAsync("alice");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "greeter-password.png"));
    }
}
