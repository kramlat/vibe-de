# vwm: the Vibe Window Manager

VDE's compositor (stage 3 of the plan). Source: `native/vwm`, built on wlroots 0.19 (vendored, static, as a
submodule under `native/third_party`). Build: `native/vwm/build.sh` → `native/build/vwm/vwm`.

## What it does

- **Wayland**: xdg-shell windows, `wlr-layer-shell` (panels; exclusive zones shrink the area windows maximize
  into), `foreign-toplevel-management` (any taskbar can list windows), xdg-decoration, xdg-output, viewporter,
  fractional-scale, data device and primary selection.
- **XWayland**: started by vwm; X11 windows are ordinary windows (title, icon, maximize...). Menus and tooltips
  (override-redirect) are shown but are not listed as windows.
- **Where it runs**: wlroots picks the backend. On a bare seat it drives the screens (DRM/libinput); inside a
  Wayland or X11 session it opens a window. That nested mode is how it is developed, and it is the fallback when
  there is no DRM. (A true X11 *window manager* mode, managing someone else's X server, is separate work.)
- **Keys**: Alt+Tab, Alt+F4, Alt+Space (window menu), Super+Space (launcher), Super+Up/Down,
  Alt+drag to move, Alt+right-drag to resize, Ctrl+Alt+Backspace (quit), Ctrl+Alt+F1..F12 (VT).

Run it: `vwm [-s program] [-l launcher] [-i socket] [-d] [-v]`. `-d` leaves every window drawing its own
titlebar.

## The shell side: Avalonia style

Titlebars and the window menu are drawn by Avalonia with the same theme tokens as every other VDE window.
vwm cannot link Avalonia, so the shell draws and vwm composites:

1. vwm decides a titlebar needs drawing (window mapped, resized, focus, title, hover, press, maximize).
2. It sends `{"event":"chrome",...}` over the control socket.
3. `vexplore --chrome` (a headless Avalonia process, `Vde.Compositor.ChromeService`) renders
   `WindowChromeView` and sends the pixels back (`chrome N W H` + premultiplied BGRA bytes).
4. vwm shows them above the window and does all hit-testing itself, so the geometry is a contract:
   `native/vwm/vwm.h` ↔ `WindowChromeView` / `WindowMenuView`.

Until a shell connects, windows get a plain bar that still moves, resizes and works. A shell that connects late
is asked to draw everything again.

Window menu (icon click, titlebar right-click, Alt+Space, or `menu N` over the socket): Restore, Move, Size,
Minimize, Maximize, Close. Double-clicking the icon closes; double-clicking the titlebar does what
`HKCU\Software\Vde\Windows\TitlebarDoubleClick` says (Maximize, Minimize, Close or None).

## Control socket

`$XDG_RUNTIME_DIR/vde/vwm.sock`, mode 0600. One JSON object per line out, one command per line in. The protocol
is documented at the top of `native/vwm/ipc.c`. `Vde.Compositor.VwmClient` is the C# side, and it is the
taskbar's `IWindowSource`, so the taskbar lists real windows when vwm runs.

## Not built yet

Snapping/tiling, virtual desktops, an Alt+Tab switcher overlay, multi-monitor layout policy, window shadows and
animations, screen capture/lock protocols, input method support, the X11 window-manager mode, and the session
glue (starting vwm, vexplore --chrome, the taskbar and the desktop together).
