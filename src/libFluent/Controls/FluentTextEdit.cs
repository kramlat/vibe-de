using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTextEdit : TextBox, IStyleable {
    Type IStyleable.StyleKey => typeof(TextBox);
}
