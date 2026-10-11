using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolstrip : ItemsControl {
    protected override Type StyleKeyOverride => typeof(ItemsControl);
}
