using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentIconView : ListBox, IStyleable {
    Type IStyleable.StyleKey => typeof(ListBox);
}
