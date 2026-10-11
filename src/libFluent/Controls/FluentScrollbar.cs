using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentScrollbar : ScrollBar, IStyleable {
    Type IStyleable.StyleKey => typeof(ScrollBar);
}
