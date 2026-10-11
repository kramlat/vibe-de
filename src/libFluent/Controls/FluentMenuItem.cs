using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenuItem : MenuItem {
    protected override Type StyleKeyOverride => typeof(MenuItem);
}
