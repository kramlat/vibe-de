using Avalonia;
using Avalonia.Controls;
using Vde.Shell;
using Vde.Shell.Controls;

namespace VExplore.Windows;

// The taskbar as a borderless window docked to one screen edge: top, bottom, left or right.
// (A real panel that reserves screen space and sits above windows needs the layer-shell client; see docs/panels.md.)
public sealed class TaskbarWindow : Window
{
    private readonly TaskbarModel _model;
    private readonly DesktopSettings? _settings;
    private bool _transparency = true;
    private bool _hidden;   // auto-hide: only a thin strip of the bar shows until the pointer reaches it
    private readonly Avalonia.Threading.DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    // Auto-hide waits while this says something (the Start menu) is open from the bar.
    public Func<bool>? KeepVisible { get; set; }

    public TaskbarWindow(TaskbarModel model, Action startMenu, Func<DateTime>? now = null, DesktopSettings? settings = null)
    {
        _model = model;
        _settings = settings;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        View = new TaskbarView(model, now);
        View.StartInvoked += (_, _) => startMenu();
        View.ButtonInvoked += (_, button) => model.Click(button);
        ApplyBackdrop();
        Content = View;
        X11WindowHints.SetType(this, X11WindowHints.Kind.Dock);
        _model.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Dock);
        Opened += (_, _) => { Dock(); if (_model.AutoHide) _hideTimer.Start(); };
        PointerEntered += (_, _) => { _hideTimer.Stop(); SetHidden(false); };
        PointerExited += (_, _) => { if (_model.AutoHide) _hideTimer.Start(); };
        _hideTimer.Tick += (_, _) =>
        {
            if (KeepVisible?.Invoke() == true) return;   // try again later
            _hideTimer.Stop();
            SetHidden(true);
        };
    }

    // Slides the bar away to a strip at its edge, or back.
    private void SetHidden(bool hidden)
    {
        if (hidden && !_model.AutoHide) return;
        if (_hidden == hidden) return;
        _hidden = hidden;
        Dock();
    }

    public bool IsHidden => _hidden && _model.AutoHide;

    public TaskbarView View { get; }

    // Follows the Colors page: the accent on the bar, and no Mica when transparency is off.
    public void FollowAppearance(Vibe.Registry.IHive hive)
    {
        void Apply()
        {
            var selection = Vde.Shell.Theme.ThemeSettings.Load(hive);
            _transparency = selection.Transparency;
            View.SetAccentFill(selection.AccentOnTaskbar);
            ApplyBackdrop();
        }
        Apply();
        hive.Changed += (_, c) => { if (c.IsUnder(Vde.Shell.Theme.ThemeSettings.KeyPath)) Avalonia.Threading.Dispatcher.UIThread.Post(Apply); };
    }

    // Sizes and places the window along the model's edge of the screen it is on.
    public void Dock()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        var area = screen.Bounds;
        var edge = _model.Edge;
        var thickness = IsHidden ? 3 : TaskbarView.Thickness;
        var pixels = (int)Math.Round(thickness * scale);
        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            Width = area.Width / scale; Height = thickness;
            Position = new PixelPoint(area.X, edge == TaskbarEdge.Top ? area.Y : area.Bottom - pixels);
        }
        else
        {
            Width = thickness; Height = area.Height / scale;
            Position = new PixelPoint(edge == TaskbarEdge.Left ? area.X : area.Right - pixels, area.Y);
        }
        ApplyBackdrop();
        View.Rebuild();
        // Keep the screen edge for the taskbar: maximised windows stop at it, and the window manager docks it there.
        long full = pixels, ax = area.X, ay = area.Y, aw = area.Width, ah = area.Height;
        if (_model.AutoHide) return;   // a hiding bar reserves nothing
        switch (edge)
        {
            case TaskbarEdge.Top: X11WindowHints.SetStrut(this, 0, 0, full, 0, 0, 0, 0, 0, ax, ax + aw - 1, 0, 0); break;
            case TaskbarEdge.Left: X11WindowHints.SetStrut(this, full, 0, 0, 0, ay, ay + ah - 1, 0, 0, 0, 0, 0, 0); break;
            case TaskbarEdge.Right: X11WindowHints.SetStrut(this, 0, full, 0, 0, 0, 0, ay, ay + ah - 1, 0, 0, 0, 0); break;
            default: X11WindowHints.SetStrut(this, 0, 0, 0, full, 0, 0, 0, 0, 0, 0, ax, ax + aw - 1); break;
        }
    }

    // The bar's Mica: the wallpaper pixels that are really behind the (docked) bar, blurred. Needs the
    // docked geometry for the crop, hence it runs from Dock and the constructor's first pass; without a
    // wallpaper the view falls back to its theme surface.
    private void ApplyBackdrop()
    {
        if (_settings is null)
            return;
        if (!_transparency)
        {
            View.SetBackdrop(null);
            return;
        }
        var blurred = WallpaperMica.Source(_settings.Wallpaper);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (blurred is null || screen is null)
        {
            View.SetBackdrop(null);
            return;
        }
        var edge = _model.Edge;
        var bar = edge switch
        {
            TaskbarEdge.Top => new Rect(0, 0, screen.Bounds.Width, TaskbarView.Thickness),
            TaskbarEdge.Left => new Rect(0, 0, TaskbarView.Thickness, screen.Bounds.Height),
            TaskbarEdge.Right => new Rect(screen.Bounds.Width - TaskbarView.Thickness, 0, TaskbarView.Thickness, screen.Bounds.Height),
            _ => new Rect(0, screen.Bounds.Height - TaskbarView.Thickness, screen.Bounds.Width, TaskbarView.Thickness),
        };
        var crop = WallpaperMica.ScreenToWallpaper(bar, new Size(blurred.PixelSize.Width, blurred.PixelSize.Height),
            new Size(screen.Bounds.Width, screen.Bounds.Height), _settings.Mode);
        View.SetBackdrop(WallpaperMica.CropBrush(blurred, crop));
    }
}
