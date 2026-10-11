using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentRadioButton : RadioButton {
    protected override Type StyleKeyOverride => typeof(RadioButton);
}
