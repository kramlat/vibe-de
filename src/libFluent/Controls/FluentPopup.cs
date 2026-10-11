using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentPopup : Popup {
    protected override Type StyleKeyOverride => typeof(Popup);
}
