using Avalonia.Controls;
using Vde.Shell;

namespace VExplore.Windows;

// One entry of a context menu: its text, the shortcut action whose key is shown beside it, what it does, and
// optionally a submenu or a check mark. A null entry in a list is a separator.
public sealed record MenuEntry(string Text, ShortcutAction? Shortcut = null, Action? Click = null, bool Enabled = true,
    IReadOnlyList<MenuEntry?>? Sub = null, bool? Checked = null);

// Fills the context menus of file windows (the desktop and file manager windows) the same way: a menu for the
// items that are selected, and a different one for the empty background of the folder or desktop, as in Explorer.
public static class FileContextMenus
{
    public static void Fill(ContextMenu menu, IEnumerable<MenuEntry?> entries, ShortcutStyle style)
    {
        menu.Items.Clear();
        foreach (var item in Items(entries, style))
            menu.Items.Add(item);
    }

    // The labels of the top level of a menu (separators skipped), for tests and screen readers.
    public static IReadOnlyList<string> Labels(ContextMenu menu)
        => menu.Items.OfType<MenuItem>().Select(m => m.Header is Grid g ? g.Children.OfType<TextBlock>().First().Text ?? "" : m.Header as string ?? "").ToList();

    private static IEnumerable<Control> Items(IEnumerable<MenuEntry?> entries, ShortcutStyle style)
    {
        // no separator first, last or twice in a row, whatever entries were left out
        var pending = false;
        var any = false;
        foreach (var entry in entries)
        {
            if (entry is null) { pending = any; continue; }
            if (pending) { yield return new Separator(); pending = false; }
            any = true;
            yield return Item(entry, style);
        }
    }

    private static MenuItem Item(MenuEntry entry, ShortcutStyle style)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 200 };
        header.Children.Add(new TextBlock { Text = entry.Text });
        var hint = new TextBlock { Text = entry.Shortcut is { } a ? ShortcutMap.Label(a, style) : "", Opacity = 0.6, Margin = new Avalonia.Thickness(24, 0, 0, 0) };
        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);
        var item = new MenuItem { Header = header, IsEnabled = entry.Enabled };
        if (entry.Checked is { } on)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = on;
        }
        if (entry.Sub is { } sub)
            foreach (var child in Items(sub, style))
                item.Items.Add(child);
        else if (entry.Click is { } click)
            item.Click += (_, _) => click();
        return item;
    }
}
