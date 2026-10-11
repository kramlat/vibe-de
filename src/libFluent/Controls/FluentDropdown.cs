using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDropdown : ComboBox {
    protected override Type StyleKeyOverride => typeof(ComboBox);
}
