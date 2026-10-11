using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentProgressBar : ProgressBar, IStyleable {
    Type IStyleable.StyleKey => typeof(ProgressBar);
}
