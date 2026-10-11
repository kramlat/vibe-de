using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentStackPanel : StackPanel, IStyleable {
    Type IStyleable.StyleKey => typeof(StackPanel);
}
