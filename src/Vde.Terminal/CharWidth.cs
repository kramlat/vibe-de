using System.Globalization;

namespace Vde.Terminal;

// How many columns a character takes: 0 (combining marks, joiners), 1, or 2 (East Asian wide and emoji).
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp < 32 || (cp >= 0x7F && cp < 0xA0)) return 0;
        if (cp < 0x300) return 1;
        if (cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0x2060 || cp == 0xFEFF || (cp >= 0xFE00 && cp <= 0xFE0F)) return 0;
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp)
        => cp >= 0x1100 && (cp <= 0x115F || cp == 0x2329 || cp == 0x232A
            || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F)
            || (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0xF900 && cp <= 0xFAFF)
            || (cp >= 0xFE30 && cp <= 0xFE6F) || (cp >= 0xFF00 && cp <= 0xFF60) || (cp >= 0xFFE0 && cp <= 0xFFE6)
            || (cp >= 0x1F300 && cp <= 0x1F64F) || (cp >= 0x1F680 && cp <= 0x1F6FF) || (cp >= 0x1F900 && cp <= 0x1F9FF)
            || (cp >= 0x1FA70 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD));
}
