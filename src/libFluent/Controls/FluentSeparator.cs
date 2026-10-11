using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSeparator : Separator, IStyleable {
    Type IStyleable.StyleKey => typeof(Separator);
}
