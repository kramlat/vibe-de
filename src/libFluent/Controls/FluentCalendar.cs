using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentCalendar : Calendar {
    protected override Type StyleKeyOverride => typeof(Calendar);
}
