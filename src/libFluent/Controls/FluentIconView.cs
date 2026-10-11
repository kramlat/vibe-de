using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentIconView : ListBox {
    protected override Type StyleKeyOverride => typeof(ListBox);
}
