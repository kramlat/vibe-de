using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToggleSwitch : ToggleSwitch, IStyleable {
    Type IStyleable.StyleKey => typeof(ToggleSwitch);
}
