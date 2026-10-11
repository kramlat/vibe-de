using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentCheckBox : CheckBox, IStyleable {
    Type IStyleable.StyleKey => typeof(CheckBox);
}
