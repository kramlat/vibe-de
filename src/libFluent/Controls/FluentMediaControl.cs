using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentMediaControl : TemplatedControl, IStyleable {
    Type IStyleable.StyleKey => typeof(TemplatedControl);
}
