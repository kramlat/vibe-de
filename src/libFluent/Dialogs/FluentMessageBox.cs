using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace libFluent.Dialogs;

public class FluentMessageBox : Window {
    public FluentMessageBox(string message) {
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();

        Content = new StackPanel {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            MinWidth = 280,
            MaxWidth = 480,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok }
        };
    }

    public static async Task Show(string message, string title = "Vibe") {
        FluentAppHost.EnsureInitialized();

        var box = new FluentMessageBox(message) { Title = title };

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            await box.ShowDialog(owner);
        else
            box.Show();
    }
}
