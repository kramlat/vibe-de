using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Vde.Shell.ComDlg;

// Asks for one line of text, or a password (shown as dots). Result is null when cancelled.
public sealed class InputWindow : Window
{
    private readonly TextBox _box;

    public InputWindow(string prompt, string title, string defaultText = "", bool password = false, Func<string, string?>? validate = null)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 420; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _box = new TextBox { Text = defaultText, PasswordChar = password ? '•' : '\0', MinWidth = 380 };
        var error = new TextBlock { IsVisible = false, Foreground = Avalonia.Media.Brushes.IndianRed };
        var ok = DialogHost.Button("OK", () =>
        {
            var problem = validate?.Invoke(_box.Text ?? "");
            error.IsVisible = problem is not null;
            error.Text = problem;
            if (problem is null) { Result = _box.Text ?? ""; Close(Result); }
        }, isDefault: true);
        var cancel = DialogHost.Button("Cancel", () => { Result = null; Close(null); }, isCancel: true);
        OkButton = ok;

        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children = { new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, _box, error, DialogHost.ButtonRow(ok, cancel) },
        };
        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string? Result { get; private set; }
    public Button OkButton { get; }
    public string Text { get => _box.Text ?? ""; set => _box.Text = value; }
}
