using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDockArea : DockPanel {
    protected override Type StyleKeyOverride => typeof(DockPanel);
}
