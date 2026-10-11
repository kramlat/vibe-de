using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentWrapPanel : WrapPanel {
    protected override Type StyleKeyOverride => typeof(WrapPanel);
}
