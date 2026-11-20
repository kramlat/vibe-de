using Avalonia.Controls;
using Avalonia.Input;
using Vde.Shell;
using Vde.Shell.Controls;
using Vibe.Registry;

namespace VExplore.Windows;

// The lock screen: a borderless, topmost, full-screen window showing the picture, time and date from the
// Lock screen settings. A slideshow moves on every ten seconds.
//
// NOT SECURE YET: this is a window like any other, so it only hides the session; it does not stop input
// reaching other windows, and any key press dismisses it. The real lock needs the compositor's session-lock
// protocol (vwm) and password checking (PAM); until then `vexplore --lock` is for looking at the design.
public sealed class LockScreenWindow : Window
{
    private readonly LockScreenView _view = new();
    private readonly Avalonia.Threading.DispatcherTimer _slides = new() { Interval = TimeSpan.FromSeconds(10) };

    public LockScreenWindow(IHive user, IHive? machine = null, Func<LockScreenSettings, string?>? statusLine = null)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        ShowInTaskbar = false;
        var settings = LockScreenSettingsStore.Load(user);
        _view.Apply(settings, DesktopSettingsStore.Load(user, machine), statusLine?.Invoke(settings));
        Content = _view;
        if (settings.Background == LockBackground.Slideshow)
        {
            _slides.Tick += (_, _) => _view.NextSlide();
            Opened += (_, _) => _slides.Start();
            Closed += (_, _) => _slides.Stop();
        }
        KeyDown += (_, e) => { if (e.Key != Key.None) Close(); };   // see the note above: not a real lock
        PointerPressed += (_, _) => Close();
    }

    public LockScreenView View => _view;
}
