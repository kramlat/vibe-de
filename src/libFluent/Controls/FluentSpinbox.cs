using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSpinbox : NumericUpDown, IStyleable {
    Type IStyleable.StyleKey => typeof(NumericUpDown);
}
