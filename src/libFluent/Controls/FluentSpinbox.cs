using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSpinbox : NumericUpDown {
    protected override Type StyleKeyOverride => typeof(NumericUpDown);
}
