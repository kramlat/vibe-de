using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDropdown : ComboBox, IStyleable {
    Type IStyleable.StyleKey => typeof(ComboBox);
}
