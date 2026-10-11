using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentBreadcrumbBar : TemplatedControl, IStyleable {
    Type IStyleable.StyleKey => typeof(TemplatedControl);
}
