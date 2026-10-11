using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentListView : ListBox, IStyleable {
    Type IStyleable.StyleKey => typeof(ListBox);
}
