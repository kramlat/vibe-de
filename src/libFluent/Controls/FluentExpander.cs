using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentExpander : Expander, IStyleable {
    Type IStyleable.StyleKey => typeof(Expander);
}
