using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentKnobControl : TemplatedControl, IStyleable {
    Type IStyleable.StyleKey => typeof(TemplatedControl);
}
