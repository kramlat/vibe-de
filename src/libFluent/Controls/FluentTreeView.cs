using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTreeView : TreeView, IStyleable {
    Type IStyleable.StyleKey => typeof(TreeView);
}
