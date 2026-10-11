using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSeparator : Separator {
    protected override Type StyleKeyOverride => typeof(Separator);
}
