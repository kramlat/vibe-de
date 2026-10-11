using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentGroupBox : GroupBox, IStyleable {
    Type IStyleable.StyleKey => typeof(GroupBox);
}
