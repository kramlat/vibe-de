using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenu : ContextMenu {
    protected override Type StyleKeyOverride => typeof(ContextMenu);
}
