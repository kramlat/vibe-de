using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentColorEdit : ColorPicker, IStyleable {
    Type IStyleable.StyleKey => typeof(ColorPicker);
}
