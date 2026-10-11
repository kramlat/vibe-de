using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentToolButton : Button, IStyleable {
    Type IStyleable.StyleKey => typeof(Button);
}
