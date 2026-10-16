using System.Text;

namespace Vde.Terminal;

public enum TerminalKey
{
    None, Enter, Tab, Backspace, Escape, Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    KeypadEnter,
}

[Flags]
public enum KeyMods { None = 0, Shift = 1, Alt = 2, Ctrl = 4 }

// What to send the program for a key press, the way xterm does.
public static class KeyEncoder
{
    public static byte[]? Encode(TerminalKey key, KeyMods mods, TerminalModes modes)
    {
        var m = 1 + ((mods & KeyMods.Shift) != 0 ? 1 : 0) + ((mods & KeyMods.Alt) != 0 ? 2 : 0) + ((mods & KeyMods.Ctrl) != 0 ? 4 : 0);   // xterm's modifier number
        string? seq = key switch
        {
            TerminalKey.Enter => modes.NewLineMode ? "\r\n" : "\r",
            TerminalKey.KeypadEnter => modes.ApplicationKeypad ? "\eOM" : "\r",
            TerminalKey.Tab => (mods & KeyMods.Shift) != 0 ? "\e[Z" : "\t",
            TerminalKey.Backspace => (mods & KeyMods.Ctrl) != 0 ? "\b" : "\x7f",
            TerminalKey.Escape => "\e",
            TerminalKey.Up => Cursor('A', m, modes),
            TerminalKey.Down => Cursor('B', m, modes),
            TerminalKey.Right => Cursor('C', m, modes),
            TerminalKey.Left => Cursor('D', m, modes),
            TerminalKey.Home => Cursor('H', m, modes),
            TerminalKey.End => Cursor('F', m, modes),
            TerminalKey.PageUp => Tilde(5, m),
            TerminalKey.PageDown => Tilde(6, m),
            TerminalKey.Insert => Tilde(2, m),
            TerminalKey.Delete => Tilde(3, m),
            TerminalKey.F1 => Function('P', m),
            TerminalKey.F2 => Function('Q', m),
            TerminalKey.F3 => Function('R', m),
            TerminalKey.F4 => Function('S', m),
            TerminalKey.F5 => Tilde(15, m),
            TerminalKey.F6 => Tilde(17, m),
            TerminalKey.F7 => Tilde(18, m),
            TerminalKey.F8 => Tilde(19, m),
            TerminalKey.F9 => Tilde(20, m),
            TerminalKey.F10 => Tilde(21, m),
            TerminalKey.F11 => Tilde(23, m),
            TerminalKey.F12 => Tilde(24, m),
            _ => null,
        };
        if (seq is null) return null;
        if (key is TerminalKey.Enter or TerminalKey.Tab or TerminalKey.Backspace && (mods & KeyMods.Alt) != 0)
            seq = "\e" + seq;   // Alt+Enter, Alt+Backspace: escape first
        return Encoding.UTF8.GetBytes(seq);
    }

    private static string Cursor(char final, int m, TerminalModes modes)
        => m > 1 ? $"\e[1;{m}{final}" : modes.ApplicationCursorKeys ? $"\eO{final}" : $"\e[{final}";

    private static string Function(char final, int m) => m > 1 ? $"\e[1;{m}{final}" : $"\eO{final}";

    private static string Tilde(int n, int m) => m > 1 ? $"\e[{n};{m}~" : $"\e[{n}~";

    // A typed character. Ctrl turns letters and a few symbols into control codes; Alt puts an Escape in front.
    public static byte[] EncodeText(string text, KeyMods mods)
    {
        if ((mods & KeyMods.Ctrl) != 0 && text.Length == 1)
        {
            var c = text[0];
            int? control = c switch
            {
                >= 'a' and <= 'z' => c - 'a' + 1,
                >= 'A' and <= 'Z' => c - 'A' + 1,
                ' ' or '@' or '2' => 0,
                '[' or '3' => 27, '\\' or '4' => 28, ']' or '5' => 29, '^' or '6' => 30, '_' or '7' or '/' => 31, '8' => 127,
                _ => null,
            };
            if (control is { } cc)
                return (mods & KeyMods.Alt) != 0 ? [0x1b, (byte)cc] : [(byte)cc];
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        return (mods & KeyMods.Alt) != 0 ? [0x1b, .. bytes] : bytes;
    }

    // Bracketed paste wraps pasted text so programs can tell it from typing.
    public static byte[] EncodePaste(string text, TerminalModes modes)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!modes.BracketedPaste) return bytes;
        return [.. "\e[200~"u8.ToArray(), .. Encoding.UTF8.GetBytes(text.Replace("\e", "")), .. "\e[201~"u8.ToArray()];   // an Escape inside would end the paste early
    }

    public enum MouseButton { Left = 0, Middle = 1, Right = 2, None = 3, WheelUp = 64, WheelDown = 65 }

    // A mouse event for a program that asked for them, or null when its tracking mode does not want this one.
    public static byte[]? EncodeMouse(MouseButton button, bool release, bool motion, int column, int row, KeyMods mods, TerminalModes modes)
    {
        if (modes.Mouse == MouseTracking.Off) return null;
        if (motion && modes.Mouse == MouseTracking.Click) return null;
        if (motion && modes.Mouse == MouseTracking.Drag && button == MouseButton.None) return null;
        var code = (int)button;
        if (motion) code += 32;
        if ((mods & KeyMods.Shift) != 0) code += 4;
        if ((mods & KeyMods.Alt) != 0) code += 8;
        if ((mods & KeyMods.Ctrl) != 0) code += 16;
        column++; row++;
        switch (modes.MouseEncoding)
        {
            case MouseEncoding.Sgr:
                return Encoding.ASCII.GetBytes($"\e[<{code};{column};{row}{(release ? 'm' : 'M')}");
            case MouseEncoding.Urxvt:
                return Encoding.ASCII.GetBytes($"\e[{(release ? 3 : code) + 32};{column};{row}M");
            default:
                if (column > 223 || row > 223) return null;   // the old encoding cannot express larger positions
                return [0x1b, (byte)'[', (byte)'M', (byte)((release ? 3 : code) + 32), (byte)(column + 32), (byte)(row + 32)];
        }
    }
}
