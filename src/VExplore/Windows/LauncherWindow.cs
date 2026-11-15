using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Vde.Freedesktop;
using Vde.Shell;
using Vde.Shell.Controls;

namespace VExplore.Windows;

// The start menu as a window. It closes when you click away or press Escape, and after you start something.
// (Anchoring it above the taskbar needs the panel work; for now it is an ordinary borderless window.)
public sealed class LauncherWindow : Window
{
    public LauncherWindow(LauncherModel model, Action<DesktopEntry> startApp, Action<ControlPanelItem> openSetting, Action<LauncherFooterAction>? footer = null, UserProfile? user = null)
    {
        Width = model.Layout == StartLayout.Classic ? 640 : 460; Height = 620;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        View = new LauncherView(model, user);
        View.AppInvoked += (_, app) => { startApp(app); Close(); };
        View.SettingInvoked += (_, item) => { openSetting(item); Close(); };
        View.FooterInvoked += (_, action) => { footer?.Invoke(action); Close(); };
        Content = View;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => Close();
        Opened += (_, _) => View.Focus_();
    }

    public LauncherView View { get; }
}
