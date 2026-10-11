using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolTip : ToolTip {
    protected override Type StyleKeyOverride => typeof(ToolTip);
}
