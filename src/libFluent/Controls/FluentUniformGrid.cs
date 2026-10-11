using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentUniformGrid : UniformGrid, IStyleable {
    Type IStyleable.StyleKey => typeof(UniformGrid);
}
