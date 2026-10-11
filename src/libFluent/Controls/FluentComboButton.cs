using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentComboButton : SplitButton, IStyleable {
    Type IStyleable.StyleKey => typeof(SplitButton);
}
