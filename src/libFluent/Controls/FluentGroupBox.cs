using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentGroupBox : HeaderedContentControl {
    protected override Type StyleKeyOverride => typeof(HeaderedContentControl);
}
