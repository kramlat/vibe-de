using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace Vde.Shell.ComDlg;

// The UAC-like consent dialog: shows exactly what will run and why it needs more rights, asks for the
// password and has a Cancel. The password goes out as a char array so the shell can wipe it after use.
public sealed class ElevationPromptWindow : Window
{
    private readonly TextBox _password = new() { PasswordChar = '•', MinWidth = 340, PlaceholderText = "Password" };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, IsVisible = false };
    private readonly StackPanel _details = new() { IsVisible = false, Spacing = 4 };
    private readonly bool _needsPassword;

    // The titlebar says which kind of prompt this is: one that only asks to agree (sudo without a password), or one that
    // wants the password.
    public static string TitleFor(ElevationRequest request) => request.NeedsPassword ? "User Account Control: password required" : "User Account Control";

    public ElevationPromptWindow(ElevationRequest request, int attempt, string? previousError)
    {
        _needsPassword = request.NeedsPassword;
        Title = TitleFor(request);
        Width = 456; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var command = new TextBox { Text = request.CommandText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), MaxHeight = 100 };
        var yes = DialogHost.Button("Yes", Accept, isDefault: true);
        var no = DialogHost.Button("No", () => { Response = ElevationResponse.Cancel; Close(); }, isCancel: true);
        yes.Tag = "yes"; no.Tag = "no";
        no.Classes.Add("accent");   // as in Windows: No is the filled button
        OkButton = yes;
        NoButton = no;
        CommandBox = command;
        if (previousError is not null)
        {
            _error.Text = previousError;
            _error.IsVisible = true;
        }

        _details.Children.Add(new TextBlock { Text = $"It needs more rights than you have: {request.Reason}.", TextWrapping = TextWrapping.Wrap });
        _details.Children.Add(new TextBlock { Text = "This is exactly what will run:" });
        _details.Children.Add(command);
        var more = new Button { Content = "Show more details", Tag = "details", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = ThemeTokensAccent() };
        more.Click += (_, _) => { _details.IsVisible = !_details.IsVisible; more.Content = _details.IsVisible ? "Hide details" : "Show more details"; };
        MoreButton = more;

        var header = new Border
        {
            Padding = new Thickness(24, 16, 24, 14), Background = ThemeTokens.Find(this, ThemeToken.Track, Brushes.LightGray),
            Child = new TextBlock { Text = "Do you want to allow this app to make changes to your device?", FontSize = 20, TextWrapping = TextWrapping.Wrap },
        };
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new ShellIcon { IconName = "dialog-password", IconSize = 40 },
                new TextBlock { Text = request.Label, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 },
            },
        });
        body.Children.Add(more);
        body.Children.Add(_details);
        if (_needsPassword)
        {
            // one password box: there is no user name or e-mail to give, the account is the one sudo names
            body.Children.Add(new TextBlock { Text = PasswordText(request, attempt), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            body.Children.Add(_password);
            body.Children.Add(_error);
        }
        var footer = new Border
        {
            Padding = new Thickness(24, 16), Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.Transparent),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8, Children = { yes, no } },
        };
        Grid.SetColumn(no, 1);
        yes.HorizontalAlignment = no.HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = new StackPanel { Children = { header, body, footer } };
        Closing += (_, _) => Response ??= ElevationResponse.Cancel;   // closing the window is saying no
        Opened += (_, _) => { if (_needsPassword) _password.Focus(); else yes.Focus(); };
    }

    private IBrush ThemeTokensAccent() => ThemeTokens.Find(this, ThemeToken.Accent, Brushes.DodgerBlue);

    // "To continue, enter the password for deck." (the account sudo is waiting for); later attempts say which one it is.
    private static string PasswordText(ElevationRequest request, int attempt)
    {
        var text = request.Account is { Length: > 0 } account ? $"To continue, enter the password for {account}." : "To continue, enter your password.";
        return attempt > 1 ? $"{text} (attempt {attempt} of {ElevationOptions.MaxAttempts})" : text;
    }

    public ElevationResponse? Response { get; private set; }
    public Button OkButton { get; }
    public Button NoButton { get; }
    public Button MoreButton { get; }
    public TextBox CommandBox { get; }
    public bool DetailsVisible => _details.IsVisible;
    public bool AsksForPassword => _needsPassword;
    public string Password { get => _password.Text ?? ""; set => _password.Text = value; }

    public void Accept()
    {
        if (!_needsPassword)
        {
            Response = new ElevationResponse(true, []);   // agreeing is all it takes: nothing to wipe
            Close();
            return;
        }
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
