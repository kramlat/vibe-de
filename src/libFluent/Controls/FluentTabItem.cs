using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTabItem : TabItem {
    protected override Type StyleKeyOverride => typeof(TabItem);
}
