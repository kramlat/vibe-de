using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentPictureBox : Image, IStyleable {
    Type IStyleable.StyleKey => typeof(Image);
}
