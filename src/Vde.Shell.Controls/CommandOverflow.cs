using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Vde.Shell;

namespace Vde.Shell.Controls;

// The "..." at the end of a command bar. Every VDE app that has no menu bar puts one there, with the things that are specific to that app
// (File Explorer: Select all, Select none, Invert selection, Properties, Options). The entries are asked for when the menu opens, so
// they can follow the app's state (Properties is grey with nothing selected).
public static class CommandOverflow
{
    public static Button Create(Func<IReadOnlyList<MenuEntry?>> entries, Func<ShortcutStyle> style, string tip = "See more")
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => FileContextMenus.Fill(menu, entries(), style());
        var button = new Button
        {
            Tag = "CommandOverflow", Content = new TextBlock { Text = "⋯", FontSize = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            Padding = new Thickness(12, 7), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, e) => { FileContextMenus.Fill(menu, entries(), style()); menu.Open(button); e.Handled = true; };
        return button;
    }
}
