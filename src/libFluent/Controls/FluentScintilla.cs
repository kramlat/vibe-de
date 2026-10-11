using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentScintilla : TemplatedControl, IStyleable {
    Type IStyleable.StyleKey => typeof(TemplatedControl);
}
