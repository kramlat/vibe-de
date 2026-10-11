using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSearchBox : AutoCompleteBox {
    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);
}
