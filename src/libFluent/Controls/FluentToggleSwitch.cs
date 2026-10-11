using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToggleSwitch : ToggleSwitch {
    protected override Type StyleKeyOverride => typeof(ToggleSwitch);
}
