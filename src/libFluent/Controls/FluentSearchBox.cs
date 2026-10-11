using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSearchBox : AutoCompleteBox, IStyleable {
    Type IStyleable.StyleKey => typeof(AutoCompleteBox);
}
