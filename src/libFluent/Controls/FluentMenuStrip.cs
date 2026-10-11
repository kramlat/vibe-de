using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenustrip : Menu, IStyleable {
    Type IStyleable.StyleKey => typeof(Menu);
}
