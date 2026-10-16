using Avalonia.Controls;
using Avalonia.Layout;

namespace Vde.Shell.ComDlg;

internal static class DialogHost
{
    // Shows the window modally over owner (or on its own if there is none) and returns when it closes.
    // Dialogs carry their answer in a Result property, so callers read that after this returns.
    public static async Task RunAsync(Window window, Window? owner)
    {
        if (owner is not null)
        {
            await window.ShowDialog(owner);
            return;
        }
        var done = new TaskCompletionSource();
        window.Closed += (_, _) => done.TrySetResult();
        window.Show();
        await done.Task;
    }

    public static Button Button(string text, Action click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinWidth = 80, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }
}
