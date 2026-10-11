using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentUniformGrid : UniformGrid {
    protected override Type StyleKeyOverride => typeof(UniformGrid);
}
