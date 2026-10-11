using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTreeView : TreeView {
    protected override Type StyleKeyOverride => typeof(TreeView);
}
