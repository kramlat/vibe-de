using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentComboButton : SplitButton {
    protected override Type StyleKeyOverride => typeof(SplitButton);
}
