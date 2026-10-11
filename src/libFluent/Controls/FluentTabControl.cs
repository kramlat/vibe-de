using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTabControl : TabControl, IStyleable {
    Type IStyleable.StyleKey => typeof(TabControl);
}
