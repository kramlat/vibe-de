using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentStatusStrip : StatusBar, IStyleable
{
    Type IStyleable.StyleKey => typeof(StatusBar);
}
