using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace libFluent.Dialogs;

public class FluentMessageBox : Window {
    public FluentMessageBox() {
        AvaloniaXamlLoader.Load(this);
    }

    public static async Task Show(string message, string title = "Fluent Desktop") {
        FluentAppHost.EnsureInitialized();

        var box = new FluentMessageBox {
            Title = title
        };

        var text = box.FindControl<TextBlock>("MessageText");
        text.Text = message;

        await box.ShowDialog(App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null);
    }
}
