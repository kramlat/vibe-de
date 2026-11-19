using Avalonia.Controls;

namespace libFluent.Windows;

public class FluentSplash : FluentWindow {
    public FluentSplash() {
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = Avalonia.Controls.WindowDecorations.None;
    }
}
