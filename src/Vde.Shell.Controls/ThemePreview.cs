using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vde.Shell.Controls;

// The little window drawn over a theme's wallpaper in the Themes and Colors previews: grey text lines and a
// button in the accent colour, light or dark, optionally with the accent on its title bar.
public static class ThemePreview
{
    public static Control MockWindow(Color accent, bool dark, bool accentTitleBar = false, double scale = 1)
    {
        var line = dark ? Color.FromRgb(0xb5, 0xb5, 0xb5) : Color.FromRgb(0xc9, 0xc9, 0xc9);
        var lines = new StackPanel { Spacing = 5 * scale };
        foreach (var width in new[] { 72.0, 62.0, 66.0 })
            lines.Children.Add(new Border { Width = width * scale, Height = 3 * scale, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(line) });
        lines.Children.Add(new Border { Width = 26 * scale, Height = 9 * scale, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accent), Margin = new Thickness(0, 5 * scale, 0, 0) });

        var body = new Border { Padding = new Thickness(11 * scale), Child = lines, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new DockPanel();
        if (accentTitleBar)
        {
            var bar = new Border { Height = 8 * scale, Background = new SolidColorBrush(accent) };
            DockPanel.SetDock(bar, Dock.Top);
            content.Children.Add(bar);
        }
        content.Children.Add(body);

        return new Border
        {
            Width = 108 * scale, Height = 68 * scale, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x2b, 0x2b, 0x2b) : Color.FromRgb(0xfa, 0xfa, 0xfa)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(0x4a, 0x4a, 0x4a) : Color.FromRgb(0xd4, 0xd4, 0xd4)),
            BorderThickness = new Thickness(accentTitleBar ? 0 : 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 14 * scale, 12 * scale),
            Child = content,
        };
    }
}
