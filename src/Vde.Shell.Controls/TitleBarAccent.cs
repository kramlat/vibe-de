using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Vde.Shell.Theme;

namespace Vde.Shell.Controls;

// Makes a window that draws its own titlebar (Explorer, the terminal) follow "Show accent color on title bars and
// window borders" the way the compositor's titlebars do: the active window's bar fills with the accent and its text and
// buttons take black or white, whichever reads; the window gets a 1px border, accent when active, the theme's border
// colour otherwise. An inactive window, or the option off, leaves the bar transparent over the window's Mica.
public static class TitleBarAccent
{
    public static void Attach(Window window, Border bar, Border body)
    {
        void Apply()
        {
            var classic = ThemeTokens.IsClassic(window);
            var on = ThemeTokens.OnAccentBar(window, window.IsActive);
            bar.Background = on ? ThemeTokens.Find(window, ThemeToken.Accent, Brushes.DodgerBlue) : Brushes.Transparent;
            // text in the bar inherits this; the caption buttons are told below
            var ink = on ? new SolidColorBrush(ThemeTokens.InkOn(ThemeTokens.AccentColor(window))) : null;
            TextElement.SetForeground(bar, ink);
            body.BorderThickness = classic || window.WindowState != WindowState.Normal ? new Thickness(0) : new Thickness(1);
            body.BorderBrush = on ? ThemeTokens.Find(window, ThemeToken.Accent, Brushes.DodgerBlue) : ThemeTokens.Find(window, ThemeToken.Border, Brushes.Gray);
            foreach (var button in bar.GetLogicalDescendants().OfType<CaptionButton>())
            {
                button.WindowActive = window.IsActive;
                button.InvalidateVisual();
            }
        }
        window.Activated += (_, _) => Apply();
        window.Deactivated += (_, _) => Apply();
        window.Opened += (_, _) => Apply();
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Apply(); };
        window.ActualThemeVariantChanged += (_, _) => Apply();
        EventHandler onTheme = (_, _) => Apply();
        window.Opened += (_, _) => ThemeTokens.Changed += onTheme;
        window.Closed += (_, _) => ThemeTokens.Changed -= onTheme;
    }
}
