using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentColorEdit : ColorPicker {
    protected override Type StyleKeyOverride => typeof(ColorPicker);
}
