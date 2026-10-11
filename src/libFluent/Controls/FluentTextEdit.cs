using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentTextEdit : TextBox {
    protected override Type StyleKeyOverride => typeof(TextBox);
}
