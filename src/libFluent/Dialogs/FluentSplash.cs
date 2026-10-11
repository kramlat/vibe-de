using Avalonia.Markup.Xaml;

namespace libFluent.Windows;

public class FluentSplash : FluentWindow {
    public FluentSplash() {
        AvaloniaXamlLoader.Load(this);
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
    }
}
