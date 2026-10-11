using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentRadioButton : RadioButton, IStyleable {
    Type IStyleable.StyleKey => typeof(RadioButton);
}
