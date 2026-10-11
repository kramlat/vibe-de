using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentListView : ListBox {
    protected override Type StyleKeyOverride => typeof(ListBox);
}
