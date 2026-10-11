using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMenustrip : Menu {
    protected override Type StyleKeyOverride => typeof(Menu);
}
