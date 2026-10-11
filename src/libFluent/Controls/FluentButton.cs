using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentButton : Button, IStyleable {
    Type IStyleable.StyleKey => typeof(Button);
}
