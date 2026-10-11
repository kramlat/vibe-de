using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentFlyout : Flyout, IStyleable {
    Type IStyleable.StyleKey => typeof(Flyout);
}
