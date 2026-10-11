using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentStackPanel : StackPanel {
    protected override Type StyleKeyOverride => typeof(StackPanel);
}
