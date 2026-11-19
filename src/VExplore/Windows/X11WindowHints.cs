using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace VExplore.Windows;

// Avalonia cannot say what kind of window a window is, and the window manager has to know: the desktop goes below
// everything, the taskbar above it against its edge, and neither gets a titlebar or a taskbar button. This sets the
// EWMH properties (_NET_WM_WINDOW_TYPE, _NET_WM_STRUT_PARTIAL) on the window's X11 id, before it is shown.
internal static class X11WindowHints
{
    public enum Kind { Desktop, Dock }

    public static void SetType(Window window, Kind kind)
    {
        Apply(window, (display, xid) =>
        {
            var type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            var value = XInternAtom(display, kind == Kind.Desktop ? "_NET_WM_WINDOW_TYPE_DESKTOP" : "_NET_WM_WINDOW_TYPE_DOCK", false);
            var atoms = new[] { value };
            XChangeProperty(display, xid, type, XaAtom, 32, 0, atoms, 1);
        });
    }

    // The screen space the window keeps for itself, in pixels from the edge: left, right, top, bottom, then each
    // edge's start and end along the screen.
    public static void SetStrut(Window window, long left, long right, long top, long bottom,
        long leftStart, long leftEnd, long rightStart, long rightEnd, long topStart, long topEnd, long bottomStart, long bottomEnd)
    {
        Apply(window, (display, xid) =>
        {
            var strut = XInternAtom(display, "_NET_WM_STRUT_PARTIAL", false);
            long[] values = [left, right, top, bottom, leftStart, leftEnd, rightStart, rightEnd, topStart, topEnd, bottomStart, bottomEnd];
            XChangeProperty(display, xid, strut, XaCardinal, 32, 0, values, values.Length);
        });
    }

    private static void Apply(Window window, Action<IntPtr, IntPtr> action)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return;
            // the EWMH properties only reach an X11 manager; under Wayland the window announces its
            // role to vwm over the control socket instead (VwmClient.AnnounceRole)
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null
                || Environment.GetEnvironmentVariable("WAYLAND_SOCKET") is not null) return;
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.HandleDescriptor != "XID") return;
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            try { action(display, handle.Handle); XFlush(display); }
            finally { XCloseDisplay(display); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no libX11: nothing to tell
        }
    }

    private const long XaAtom = 4, XaCardinal = 6;

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern long XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window, long property, long type, int format, int mode, long[] data, int count);
}
