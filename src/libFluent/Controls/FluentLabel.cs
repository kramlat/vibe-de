using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentLabel : TextBlock {
    protected override Type StyleKeyOverride => typeof(TextBlock);
}
