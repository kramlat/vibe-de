using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentProgressBar : ProgressBar {
    protected override Type StyleKeyOverride => typeof(ProgressBar);
}
