using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTabItem : TabItem, IStyleable {
    Type IStyleable.StyleKey => typeof(TabItem);
}
