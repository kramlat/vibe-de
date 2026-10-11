using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentWrapPanel : WrapPanel, IStyleable {
    Type IStyleable.StyleKey => typeof(WrapPanel);
}
