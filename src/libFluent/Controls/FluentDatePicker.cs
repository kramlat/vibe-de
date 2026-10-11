using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDatePicker : DatePicker, IStyleable {
    Type IStyleable.StyleKey => typeof(DatePicker);
}
