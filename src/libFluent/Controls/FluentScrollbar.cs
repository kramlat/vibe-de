using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentScrollbar : ScrollBar {
    protected override Type StyleKeyOverride => typeof(ScrollBar);
}
