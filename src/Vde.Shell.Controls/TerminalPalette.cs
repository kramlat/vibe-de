using Avalonia.Media;
using Vde.Terminal;

namespace Vde.Shell.Controls;

// What the terminal's colours look like: the default foreground and background, the 16 named colours, and how the
// 256-colour cube and grey ramp are derived. The defaults follow the shell theme (see TerminalView).
public sealed class TerminalPalette
{
    public Color Foreground { get; init; } = Color.Parse("#CCCCCC");
    public Color Background { get; init; } = Color.Parse("#1E1E1E");
    public Color Cursor { get; init; } = Color.Parse("#FFFFFF");
    public Color Selection { get; init; } = Color.Parse("#264F78");
    public IReadOnlyList<Color> Ansi { get; init; } = Dark;

    public static readonly IReadOnlyList<Color> Dark = Parse("#0C0C0C #C50F1F #13A10E #C19C00 #0037DA #881798 #3A96DD #CCCCCC #767676 #E74856 #16C60C #F9F1A5 #3B78FF #B4009E #61D6D6 #F2F2F2");
    public static readonly IReadOnlyList<Color> Light = Parse("#000000 #C50F1F #13A10E #C19C00 #0037DA #881798 #3A96DD #CCCCCC #767676 #E74856 #16C60C #B5A000 #3B78FF #B4009E #3A96DD #F2F2F2");

    private static IReadOnlyList<Color> Parse(string list) => list.Split(' ').Select(Color.Parse).ToList();

    public static TerminalPalette ForBackground(Color background, Color foreground, Color accent)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return new TerminalPalette
        {
            Foreground = foreground, Background = background, Cursor = foreground,
            Selection = Color.FromArgb(110, accent.R, accent.G, accent.B),
            Ansi = luminance > 0.5 ? Light : Dark,
        };
    }

    public Color Resolve(TermColor color, bool foreground)
    {
        switch (color.Kind)
        {
            case ColorKind.Rgb: return Color.FromRgb((byte)(color.Value >> 16), (byte)(color.Value >> 8), (byte)color.Value);
            case ColorKind.Indexed:
                var i = (int)color.Value;
                if (i < 16) return Ansi[i];
                if (i < 232)
                {
                    i -= 16;
                    byte Level(int n) => (byte)(n == 0 ? 0 : 55 + n * 40);
                    return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
                }
                var grey = (byte)(8 + (i - 232) * 10);
                return Color.FromRgb(grey, grey, grey);
            default: return foreground ? Foreground : Background;
        }
    }
}
