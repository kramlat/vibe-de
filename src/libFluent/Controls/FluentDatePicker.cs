using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDatePicker : DatePicker {
    protected override Type StyleKeyOverride => typeof(DatePicker);
}
