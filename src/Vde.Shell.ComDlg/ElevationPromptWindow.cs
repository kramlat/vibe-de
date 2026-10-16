using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Controls;

namespace Vde.Shell.ComDlg;

// The UAC-like consent dialog: shows exactly what will run and why it needs more rights, asks for the
// password and has a Cancel. The password goes out as a char array so the shell can wipe it after use.
public sealed class ElevationPromptWindow : Window
{
    private readonly TextBox _password = new() { PasswordChar = '•', MinWidth = 340 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, IsVisible = false };

    public ElevationPromptWindow(ElevationRequest request, int attempt, string? previousError)
    {
        Title = "Permission needed";
        Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var command = new TextBox { Text = request.CommandText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), MaxHeight = 100 };
        var ok = DialogHost.Button("Allow", Accept, isDefault: true);
        var cancel = DialogHost.Button("Cancel", () => { Response = ElevationResponse.Cancel; Close(); }, isCancel: true);
        OkButton = ok;
        CommandBox = command;
        if (previousError is not null)
        {
            _error.Text = previousError;
            _error.IsVisible = true;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
                {
                    new ShellIcon { IconName = "dialog-password", IconSize = 32 },
                    new TextBlock { Text = $"Allow \"{request.Label}\" to make changes?", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                } },
                new TextBlock { Text = $"It needs more rights than you have: {request.Reason}.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "This is exactly what will run:" },
                command,
                new TextBlock { Text = PasswordLabel(request, attempt) },
                _password,
                _error,
                DialogHost.ButtonRow(ok, cancel),
            },
        };
        Closing += (_, _) => Response ??= ElevationResponse.Cancel;   // closing the window is saying no
        Opened += (_, _) => _password.Focus();
    }

    // "Password for deck" when sudo wants a particular account's password; "Your password" otherwise.
    private static string PasswordLabel(ElevationRequest request, int attempt)
    {
        var label = request.Account is { Length: > 0 } account ? $"Password for {account}" : attempt > 1 ? "Password" : "Your password";
        return attempt > 1 ? $"{label} (attempt {attempt} of {ElevationOptions.MaxAttempts})" : label;
    }

    public ElevationResponse? Response { get; private set; }
    public Button OkButton { get; }
    public TextBox CommandBox { get; }
    public string Password { get => _password.Text ?? ""; set => _password.Text = value; }

    public void Accept()
    {
        if (Password.Length == 0)
        {
            _error.Text = "Type your password.";
            _error.IsVisible = true;
            return;
        }
        Response = new ElevationResponse(true, Password.ToCharArray());
        _password.Text = "";   // do not leave it sitting in the control
        Close();
    }
}

// Plugs the dialog into the shell's elevation flow.
public sealed class ElevationPromptDialog(Func<Window?> owner) : IElevationPrompt
{
    // Called with each prompt window as it opens (tests use it to answer the dialog).
    public Action<ElevationPromptWindow>? Shown { get; init; }

    public async Task<ElevationResponse> AskAsync(ElevationRequest request, int attempt, string? previousError, CancellationToken cancel)
    {
        var window = new ElevationPromptWindow(request, attempt, previousError);
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        using var registration = cancel.Register(() => window.Close());
        if (owner() is { } parent)
            _ = window.ShowDialog(parent);
        else
            window.Show();
        Shown?.Invoke(window);
        await closed.Task;
        return window.Response ?? ElevationResponse.Cancel;
    }
}
