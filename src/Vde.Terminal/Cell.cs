namespace Vde.Terminal;

public enum ColorKind : byte { Default, Indexed, Rgb }

// A colour as programs ask for it: the default, one of the 256 palette entries, or true colour. The view decides what
// they look like (the palette comes from the theme).
public readonly record struct TermColor(ColorKind Kind, uint Value)
{
    public static readonly TermColor Default = default;
    public static TermColor Index(int i) => new(ColorKind.Indexed, (uint)i);
    public static TermColor Rgb(byte r, byte g, byte b) => new(ColorKind.Rgb, (uint)(r << 16 | g << 8 | b));
}

[Flags]
public enum CellAttrs : ushort
{
    None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8, Blink = 16, Inverse = 32, Invisible = 64, Strike = 128,
    Overline = 256, DoubleUnderline = 512,
}

// One character position. A wide character occupies two: the first holds it (Width 2), the second is its
// continuation (Width 0). Rune 0 is an empty cell.
public struct Cell
{
    public int Rune;
    public string? Combining;   // zero-width characters that follow, e.g. an accent
    public TermColor Fg, Bg;
    public CellAttrs Attrs;
    public ushort Link;         // index into the emulator's hyperlink table (OSC 8), 0 = none
    public byte Width;          // 1, 2, or 0 for the second half of a wide character

    public readonly bool IsEmpty => Rune == 0;
    public readonly string Text => Rune == 0 ? " " : char.ConvertFromUtf32(Rune) + Combining;
}

public enum LineMark : byte { None, PromptStart }

public sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;   // continues on the next line (the text was longer than the width)
    public LineMark Mark;  // shell integration: a prompt starts on this line
    public int? ExitCode;  // ... and the command that ran before it ended with this (OSC 133;D)

    public Line(int columns) => Cells = new Cell[columns];

    public void Resize(int columns)
    {
        if (columns != Cells.Length)
            Array.Resize(ref Cells, columns);
    }

    public Line Clone()
    {
        var copy = new Line(Cells.Length) { Wrapped = Wrapped, Mark = Mark, ExitCode = ExitCode };
        Array.Copy(Cells, copy.Cells, Cells.Length);
        return copy;
    }

    // The text of the line without trailing blanks.
    public string Text(int from = 0, int to = int.MaxValue)
    {
        var sb = new System.Text.StringBuilder();
        var end = Math.Min(to, Cells.Length - 1);
        for (var i = from; i <= end; i++)
        {
            if (Cells[i].Width == 0 && i > 0) continue;
            sb.Append(Cells[i].IsEmpty ? ' ' : Cells[i].Text);
        }
        return sb.ToString().TrimEnd(' ');
    }
}
