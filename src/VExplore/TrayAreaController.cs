using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using Vde.Shell.Controls;
using Vde.Shell.Tray;
using TrayIcon = Vde.Shell.Tray.TrayIcon;

namespace VExplore;

// Joins the session bus as the StatusNotifierWatcher host and turns the live icons into buttons in
// the taskbar's tray area: freedesktop icon names by way of ShellIcon, pixmaps straight from the
// item, menus by way of com.canonical.dbusmenu. Left click activates the item (falling back to its
// menu when there is nothing to activate); right click shows its menu.
public sealed class TrayAreaController : IDisposable
{
    private readonly TaskbarView _taskbar;
    private readonly Action _changed;
    private TrayHost? _host;

    public TrayAreaController(TaskbarView taskbar, Action changed)
    {
        _taskbar = taskbar;
        _changed = changed;
    }

    public async Task StartAsync()
    {
        var host = await TrayHost.StartAsync();
        if (host is null) return;
        _host = host;
        // Watcher and item events arrive on DBus threads; everything visible hops to the UI thread.
        host.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(host, _host)) return;
            Populate(host);
            _changed();
        });
        if (ReferenceEquals(host, _host))
            Populate(host);
    }

    public void Dispose() => _host?.Dispose();

    private void Populate(TrayHost host)
    {
        _taskbar.TrayArea.Children.Clear();
        foreach (var icon in host.Icons)
            _taskbar.TrayArea.Children.Add(MakeButton(host, icon));
    }

    private Button MakeButton(TrayHost host, TrayIcon icon)
    {
        var button = new Button
        {
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Width = 24, Height = 24, Tag = icon,
        };
        if (icon.BestIconName.Length > 0)
            button.Content = new ShellIcon { IconName = icon.BestIconName, IconSize = 24 };
        else if (icon.BestPixmap is { } pixmap && TryBitmap(pixmap) is { } image)
            button.Content = new Image { Source = image, Width = pixmap.Width, Height = pixmap.Height };
        else
            button.Content = new TextBlock { Text = "·", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, FontSize = 18 };

        if (icon.ToolTip is { } tip && (tip.Title.Length > 0 || tip.Body.Length > 0))
            ToolTip.SetTip(button, tip.Title.Length > 0 ? tip.Title + "\n" + tip.Body : tip.Body);
        else if (icon.DisplayName.Length > 0)
            ToolTip.SetTip(button, icon.DisplayName);

        button.Click += (_, _) => { _ = ActivateAsync(host, icon, button); };
        // Right click: the item's bus menu, or a secondary activation when it has none.
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased || e.Handled) return;
            e.Handled = true;
            var menu = host.Menu(icon);
            if (menu is null)
            {
                _ = host.Item(icon)?.SecondaryActivateAsync(-1, -1);
                return;
            }
            _ = ShowMenuAsync(menu, button);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return button;
    }

    // Activate, and when the item cannot be activated (AppIndicator-style items export nothing
    // there) its menu is the click's effect.
    private static async Task ActivateAsync(TrayHost host, TrayIcon icon, Button button)
    {
        try
        {
            var item = host.Item(icon);
            if (item is null) return;
            var point = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
            var activation = item.ActivateAsync(point.X, point.Y).WaitAsync(TimeSpan.FromMilliseconds(750));
            if (item.MenuPath.Length == 0)
            {
                await activation;
                return;
            }
            try { await activation; }
            catch (TimeoutException)
            {
                if (host.Menu(icon) is { } menu) await ShowMenuAsync(menu, button);
            }
        }
        catch (Exception)
        {
            // A dead item sorts itself out through NameOwnerChanged.
        }
    }

    // Reads the item's menu and shows it over the button. Menu reading happens per open: apps fill
    // and revise their menus lazily.
    private static async Task ShowMenuAsync(DBusMenuClient menu, Control anchor)
    {
        try
        {
            _ = await menu.AboutToShowAsync(0);   // gives apps a chance to fill before we read
            var snapshot = await menu.ReadAsync();
            var rows = MenuItems(snapshot.VisibleItems, menu);
            if (rows.Count == 0) return;
            new ContextMenu { ItemsSource = rows }.Open(anchor);
        }
        catch (Exception)
        {
            // No menu came back: nothing to show.
        }
    }

    private static Avalonia.Controls.Controls MenuItems(IReadOnlyList<TrayMenuItem> rows, DBusMenuClient menu)
    {
        var items = new Avalonia.Controls.Controls();
        foreach (var row in rows)
        {
            if (!row.Visible) continue;
            if (row.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = row.DisplayLabel, IsEnabled = row.Enabled, Tag = row.Id };
            if (row.ToggleState >= 0)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = row.ToggleState == 1;
            }
            if (row.Children.Count > 0)
                item.ItemsSource = MenuItems(row.Children, menu);
            else
                item.Click += (_, _) => { _ = menu.SendEventAsync(row.Id, row.ToggleState >= 0 ? "toggled" : "clicked", null); };
            items.Add(item);
        }
        return items;
    }

    // SNI pixmaps are ARG B32 in network byte order — bytes are A,R,G,B — while Avalonia's
    // Bgra8888 wants premultiplied BGRA.
    private static IImage? TryBitmap(TrayPixmap pixmap)
    {
        if (pixmap.Width <= 0 || pixmap.Height <= 0 || pixmap.Width * pixmap.Height * 4 > pixmap.Argb.Length) return null;
        try
        {
            var bytes = pixmap.Argb;
            var output = new byte[pixmap.Width * pixmap.Height * 4];
            for (int i = 0; i < output.Length; i += 4)
            {
                byte a = bytes[i], r = bytes[i + 1], g = bytes[i + 2], b = bytes[i + 3];
                output[i] = (byte)(b * a / 255);
                output[i + 1] = (byte)(g * a / 255);
                output[i + 2] = (byte)(r * a / 255);
                output[i + 3] = a;
            }
            var bitmap = new WriteableBitmap(new PixelSize(pixmap.Width, pixmap.Height), new Vector(96, 96), PixelFormats.Bgra8888);
            using (var frame = bitmap.Lock())
            {
                for (int y = 0; y < pixmap.Height; y++)
                    Marshal.Copy(output, y * pixmap.Width * 4, frame.Address + y * frame.RowBytes, pixmap.Width * 4);
            }
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
