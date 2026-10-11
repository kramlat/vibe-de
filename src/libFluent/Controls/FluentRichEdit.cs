using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentRichEdit : TextBox {
    protected override Type StyleKeyOverride => typeof(TextBox);
}
