using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTimePicker : TimePicker {
    protected override Type StyleKeyOverride => typeof(TimePicker);
}
