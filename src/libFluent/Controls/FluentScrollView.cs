using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentScrollView : ScrollViewer {
    protected override Type StyleKeyOverride => typeof(ScrollViewer);
}
