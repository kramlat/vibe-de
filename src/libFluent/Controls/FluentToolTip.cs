using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolTip : ToolTip, IStyleable {
    Type IStyleable.StyleKey => typeof(ToolTip);
}
