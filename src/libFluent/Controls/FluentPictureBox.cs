using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentPictureBox : Image {
    protected override Type StyleKeyOverride => typeof(Image);
}
