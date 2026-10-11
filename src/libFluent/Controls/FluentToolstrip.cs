using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolstrip : ToolBar, IStyleable {
    Type IStyleable.StyleKey => typeof(ToolBar);
}
