using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentPopup : Popup, IStyleable {
    Type IStyleable.StyleKey => typeof(Popup);
}
