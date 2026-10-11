using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTimePicker : TimePicker, IStyleable {
    Type IStyleable.StyleKey => typeof(TimePicker);
}
