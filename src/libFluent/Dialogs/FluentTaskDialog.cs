using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace libFluent.Dialogs;

public class FluentTaskDialog : Window {
    public FluentTaskDialog()     {
        AvaloniaXamlLoader.Load(this);
    }
}
