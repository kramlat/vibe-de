namespace Vde.Shell;

public enum ShortcutAction
{
    Copy, Cut, Paste, SelectAll, NewFolder, Rename, Delete, DeletePermanently, Open,
    Back, Forward, Up, Refresh, FocusAddress,
}

[Flags]
public enum Mods { None = 0, Primary = 1, Shift = 2, Alt = 4 }   // Primary is Ctrl on a PC keyboard and Command on an Apple one

// A key with modifiers, as written in the table: key names are the ones Avalonia's Key enum uses
// ("C", "Delete", "Left", "OemOpenBrackets"...).
public sealed record Shortcut(string Key, Mods Mods = Mods.None);

// What was actually pressed, with the real modifier keys.
public readonly record struct KeyPress(string Key, bool Ctrl, bool Shift, bool Alt, bool Meta);

// The shortcuts of the shell's file windows, for both styles, and how to write them down for a menu.
// PC style is Ctrl-based. Mac style uses Command (the Meta/Super key) where PC uses Ctrl, and the Mac
// conventions where they differ: Command+Delete moves to the trash, Command+[ goes back, Command+R refreshes.
public static class ShortcutMap
{
    private static readonly Dictionary<ShortcutAction, Shortcut[]> Pc = new()
    {
        [ShortcutAction.Copy] = [new("C", Mods.Primary)],
        [ShortcutAction.Cut] = [new("X", Mods.Primary)],
        [ShortcutAction.Paste] = [new("V", Mods.Primary)],
        [ShortcutAction.SelectAll] = [new("A", Mods.Primary)],
        [ShortcutAction.NewFolder] = [new("N", Mods.Primary | Mods.Shift)],
        [ShortcutAction.Rename] = [new("F2")],
        [ShortcutAction.Delete] = [new("Delete")],
        [ShortcutAction.DeletePermanently] = [new("Delete", Mods.Shift)],
        [ShortcutAction.Open] = [new("Enter")],
        [ShortcutAction.Back] = [new("Left", Mods.Alt)],
        [ShortcutAction.Forward] = [new("Right", Mods.Alt)],
        [ShortcutAction.Up] = [new("Up", Mods.Alt), new("Back")],
        [ShortcutAction.Refresh] = [new("F5")],
        [ShortcutAction.FocusAddress] = [new("L", Mods.Primary)],
    };

    private static readonly Dictionary<ShortcutAction, Shortcut[]> Mac = new()
    {
        [ShortcutAction.Copy] = [new("C", Mods.Primary)],
        [ShortcutAction.Cut] = [new("X", Mods.Primary)],
        [ShortcutAction.Paste] = [new("V", Mods.Primary)],
        [ShortcutAction.SelectAll] = [new("A", Mods.Primary)],
        [ShortcutAction.NewFolder] = [new("N", Mods.Primary | Mods.Shift)],
        [ShortcutAction.Rename] = [new("F2"), new("Enter")],                       // Return renames, as in Finder
        [ShortcutAction.Delete] = [new("Back", Mods.Primary), new("Delete")],      // ⌘⌫ moves to the trash
        [ShortcutAction.DeletePermanently] = [new("Back", Mods.Primary | Mods.Alt), new("Delete", Mods.Shift)],   // ⌥⌘⌫
        [ShortcutAction.Open] = [new("O", Mods.Primary), new("Down", Mods.Primary)],
        [ShortcutAction.Back] = [new("OemOpenBrackets", Mods.Primary)],            // ⌘[
        [ShortcutAction.Forward] = [new("OemCloseBrackets", Mods.Primary)],        // ⌘]
        [ShortcutAction.Up] = [new("Up", Mods.Primary)],                           // ⌘↑
        [ShortcutAction.Refresh] = [new("R", Mods.Primary)],
        [ShortcutAction.FocusAddress] = [new("L", Mods.Primary)],
    };

    public static IReadOnlyList<Shortcut> For(ShortcutAction action, ShortcutStyle style)
        => (style == ShortcutStyle.Mac ? Mac : Pc)[action];

    // The action a key press means, or null. The modifiers must match exactly: Ctrl+Shift+N is not Ctrl+N.
    public static ShortcutAction? Match(KeyPress press, ShortcutStyle style)
    {
        // On a PC, Primary is Ctrl (Meta is the Windows key and means nothing here); on an Apple keyboard it is Command.
        bool primary = style == ShortcutStyle.Mac ? press.Meta : press.Ctrl;
        bool strayPrimary = style == ShortcutStyle.Mac ? press.Ctrl : press.Meta;   // the other style's key: not ours
        if (strayPrimary)
            return null;

        var mods = (primary ? Mods.Primary : Mods.None) | (press.Shift ? Mods.Shift : Mods.None) | (press.Alt ? Mods.Alt : Mods.None);
        foreach (var (action, shortcuts) in style == ShortcutStyle.Mac ? Mac : Pc)
            if (shortcuts.Any(s => s.Key == press.Key && s.Mods == mods))
                return action;
        return null;
    }

    // "Ctrl+Shift+N" for a PC, "⇧⌘N" for a Mac (modifiers in Apple's order: ⌃ ⌥ ⇧ ⌘, then the key).
    public static string Format(Shortcut shortcut, ShortcutStyle style)
    {
        if (style == ShortcutStyle.Pc)
        {
            var parts = new List<string>();
            if (shortcut.Mods.HasFlag(Mods.Primary)) parts.Add("Ctrl");
            if (shortcut.Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
            if (shortcut.Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
            parts.Add(PcKey(shortcut.Key));
            return string.Join('+', parts);
        }
        return (shortcut.Mods.HasFlag(Mods.Alt) ? "⌥" : "") + (shortcut.Mods.HasFlag(Mods.Shift) ? "⇧" : "")
            + (shortcut.Mods.HasFlag(Mods.Primary) ? "⌘" : "") + MacKey(shortcut.Key);
    }

    // The first shortcut of an action, written for the style: what a menu shows next to an item.
    public static string Label(ShortcutAction action, ShortcutStyle style) => Format(For(action, style)[0], style);

    private static string PcKey(string key) => key switch
    {
        "Back" => "Backspace", "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓",
        "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", _ => key,
    };

    private static string MacKey(string key) => key switch
    {
        "Back" => "⌫", "Delete" => "⌦", "Enter" => "↩", "Escape" => "⎋", "Tab" => "⇥", "Space" => "Space",
        "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓",
        "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", _ => key,
    };
}
