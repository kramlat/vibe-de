using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentScrollView : ScrollViewer, IStyleable {
    Type IStyleable.StyleKey => typeof(ScrollViewer);
}
