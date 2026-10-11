using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolButton : Button {
    protected override Type StyleKeyOverride => typeof(Button);
}
