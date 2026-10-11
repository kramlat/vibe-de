using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentRichEdit : RichTextBox, IStyleable {
    Type IStyleable.StyleKey => typeof(RichTextBox);
}
