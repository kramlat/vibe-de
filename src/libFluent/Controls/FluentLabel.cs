using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentLabel : TextBlock, IStyleable {
    Type IStyleable.StyleKey => typeof(TextBlock);
}
