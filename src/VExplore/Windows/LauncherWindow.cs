using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;
using Vde.Shell.Theme;

namespace VExplore.Windows;

// The start menu as a window. It closes when you click away or press Escape, and after you start something.
// (Anchoring it above the taskbar needs the panel work; for now it is an ordinary borderless window.)
public sealed class LauncherWindow : Window
{
    public LauncherWindow(LauncherModel model, Action<DesktopEntry> startApp, Action<ControlPanelItem> openSetting, Action<LauncherFooterAction>? footer = null, UserProfile? user = null, DesktopSettings? settings = null)
    {
        bool modern = model.Layout == StartLayout.Modern;
        Width = modern ? 584 : 640; Height = 620;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;

        View = new LauncherView(model, user);
        View.AppInvoked += (_, app) => { startApp(app); Close(); };
        View.SettingInvoked += (_, item) => { openSetting(item); Close(); };
        View.FooterInvoked += (_, action) => { footer?.Invoke(action); Close(); };

        if (modern)
        {
            // Windows 11 mode: the menu floats as a rounded sheet of Mica, so the window itself is
            // transparent and the crop goes on the border. Everything must clip to the radius.
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            var body = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = Brushes.White,
                Child = View,
            };
            Opened += (_, _) => body.Background = ThemeTokens.Find(this, ThemeToken.Surface, Brushes.White);
            WindowMica.Attach(this, settings, body);   // subscribed after the surface colour, so it paints over it
            Content = body;
        }
        else
        {
            Content = View;
            WindowMica.Attach(this, settings);
        }

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => Close();
        Opened += (_, _) => View.Focus_();
    }

    public LauncherView View { get; }
}
