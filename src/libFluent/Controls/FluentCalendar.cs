using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentCalendar : Calendar, IStyleable {
    Type IStyleable.StyleKey => typeof(Calendar);
}
