using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenuItem : MenuItem, IStyleable {
    Type IStyleable.StyleKey => typeof(MenuItem);
}
