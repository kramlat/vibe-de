using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTabControl : TabControl {
    protected override Type StyleKeyOverride => typeof(TabControl);
}
