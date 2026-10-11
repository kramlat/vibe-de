using Avalonia.Controls;
using Avalonia.Styling;

namespace libFluent.Controls;

public class FluentDataGrid : DataGrid, IStyleable {
    Type IStyleable.StyleKey => typeof(DataGrid);
}
