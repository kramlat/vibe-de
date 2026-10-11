using Avalonia.Controls.Documents;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentHyperlink : Hyperlink, IStyleable {
    Type IStyleable.StyleKey => typeof(Hyperlink);
}
