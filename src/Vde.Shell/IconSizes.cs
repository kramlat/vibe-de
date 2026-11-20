namespace Vde.Shell;

// The icon sizes the desktop uses, the same ones KDE's Kirigami calls small, smallMedium, medium, large, huge and
// enormous: they are the sizes icon themes (Breeze among them) are drawn at, so an icon asked for at one of these is
// the theme's own picture, crisp, and never a picture stretched by a few pixels. Anything else is moved to the
// nearest of them (an 18 pixel icon becomes 16, a 20 or 24 pixel one 22).
public static class IconSizes
{
    public const int Small = 16, SmallMedium = 22, Medium = 32, Large = 48, Huge = 64, Enormous = 128;

    // What themes ship plus the bigger steps the desktop view offers.
    public static readonly IReadOnlyList<int> All = [16, 22, 32, 48, 64, 96, 128, 160, 192, 224, 256];

    public static int Snap(int size)
    {
        if (size <= All[0]) return All[0];
        var best = All[0];
        foreach (var candidate in All)
            if (Math.Abs(candidate - size) < Math.Abs(best - size) || (Math.Abs(candidate - size) == Math.Abs(best - size) && candidate < best))
                best = candidate;
        return best;
    }
}

// How big a desktop icon's cell is, worked out the way Plasma's desktop (plasma-desktop, FolderView.qml) does it:
// the cell is the icon plus room for the label, and whatever is left over at the screen's edge is shared out
// between the cells so the grid fills the screen without a gap and without putting anything on a fractional pixel.
public sealed record DesktopMetrics(int IconSize = IconSizes.Large, int TextLines = 2, int LabelWidth = 1, double GridUnit = 18, double SmallSpacing = 4)
{
    // The icon sizes the settings page offers (Plasma's list from 22 up to 256, thinned out).
    public static readonly IReadOnlyList<int> Choices = [32, 48, 64, 96, 128];

    public int Icon => IconSizes.Snap(IconSize);

    // The narrowest a cell can be: the icon with margins, and wide enough for a label of the chosen width.
    public double MinCellWidth => Math.Max(Icon + 2 * GridUnit + 2 * SmallSpacing, IconSizes.Small * (LabelWidth * 2 + 4));

    // The icon, the label's lines, and three gaps.
    public double MinCellHeight => Icon + GridUnit * TextLines + SmallSpacing * 3;

    // The share of the left-over space each cell gets (whole pixels).
    public static double ExtraSpacing(double cell, double container)
    {
        int columns = (int)Math.Floor(container / cell);
        if (columns <= 0) return 0;
        double extra = Math.Max(container - columns * cell, 0);
        return Math.Floor(extra / columns);
    }

    public double CellWidth(double viewportWidth) => MinCellWidth + ExtraSpacing(MinCellWidth, viewportWidth);
    public double CellHeight(double viewportHeight) => MinCellHeight + ExtraSpacing(MinCellHeight, viewportHeight);

    // Where the highlight frame sits in a cell and how big it is (Plasma: smallSpacing in from each side).
    public double FrameWidth(double cellWidth) => cellWidth - 2 * SmallSpacing;
}
