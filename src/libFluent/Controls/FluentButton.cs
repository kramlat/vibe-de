using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentButton : Button {
    protected override Type StyleKeyOverride => typeof(Button);
}
