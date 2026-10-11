using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenu : ContextMenu, IStyleable {
    Type IStyleable.StyleKey => typeof(ContextMenu);
}
