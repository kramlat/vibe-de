using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentRichLabel : TextBlock, IStyleable {
    Type IStyleable.StyleKey => typeof(TextBlock);
}
