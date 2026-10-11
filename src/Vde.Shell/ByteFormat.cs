using System.Globalization;

namespace Vde.Shell;

public static class ByteFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    // Explorer-style sizes: 1024 steps, three significant digits ("1.46 GB", "14.6 GB", "146 GB").
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024)
            return bytes.ToString(culture) + " " + (bytes == 1 ? "byte" : "bytes");

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string text = value >= 100 ? value.ToString("0", culture)
            : value >= 10 ? value.ToString("0.0", culture)
            : value.ToString("0.00", culture);
        return text + " " + Units[unit];
    }
}
