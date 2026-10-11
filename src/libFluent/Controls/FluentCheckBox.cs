using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentCheckBox : CheckBox {
    protected override Type StyleKeyOverride => typeof(CheckBox);
}
