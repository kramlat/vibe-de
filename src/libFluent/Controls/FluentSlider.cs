using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentSlider : Slider, IStyleable {
    Type IStyleable.StyleKey => typeof(Slider);
}
