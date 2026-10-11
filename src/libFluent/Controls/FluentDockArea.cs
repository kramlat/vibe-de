using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDockArea : DockPanel, IStyleable {
    Type IStyleable.StyleKey => typeof(DockPanel);
}
