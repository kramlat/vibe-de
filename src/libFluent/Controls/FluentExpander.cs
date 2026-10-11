using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentExpander : Expander {
    protected override Type StyleKeyOverride => typeof(Expander);
}
