using Avalonia.Markup.Xaml;

namespace libFluent.Windows;

public class FluentToolWindow : FluentWindow {
    public FluentToolWindow() {
        AvaloniaXamlLoader.Load(this);
    }
}
