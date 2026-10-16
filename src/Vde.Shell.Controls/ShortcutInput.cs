using Avalonia.Input;
using Vde.Shell;

namespace Vde.Shell.Controls;

public static class ShortcutInput
{
    // Avalonia's key names are the ones the shortcut table uses, except where two names share one key.
    public static string KeyName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Back => "Back",
        Key.OemOpenBrackets => "OemOpenBrackets",
        Key.OemCloseBrackets => "OemCloseBrackets",
        _ => key.ToString(),
    };

    public static KeyPress From(KeyEventArgs e) => new(
        KeyName(e.Key),
        e.KeyModifiers.HasFlag(KeyModifiers.Control),
        e.KeyModifiers.HasFlag(KeyModifiers.Shift),
        e.KeyModifiers.HasFlag(KeyModifiers.Alt),
        e.KeyModifiers.HasFlag(KeyModifiers.Meta));
}
