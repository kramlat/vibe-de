# vwm: the Vibe Window Manager

VDE's compositor (stage 3 of the plan). Source: `native/vwm`, built on wlroots 0.19 (vendored, static, as a
submodule under `native/third_party`). Build: `native/vwm/build.sh` → `native/build/vwm/vwm`.

## What it does

- **Wayland**: xdg-shell windows, `wlr-layer-shell` (panels; exclusive zones shrink the area windows maximize
  into), `foreign-toplevel-management` (any taskbar can list windows), xdg-decoration, xdg-output, viewporter,
  fractional-scale, data device and primary selection. The protocols modern toolkits expect are there too:
  xdg-activation (the compositor focuses a surface an app asks for, e.g. a new browser window),
  cursor-shape (Chromium and Electron set cursors by name), single-pixel-buffer, content-type,
  wlroots data-control (clipboard managers), ext-idle-notify + idle-inhibit (screen stays on while a
  presentation runs), and presentation-time. Chromium/Electron (VS Code, Edge, Chromium, Chrome) run
  Wayland-native against it and open focused.
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

A client that asks for client-side decorations over xdg-decoration (Chromium and its variants such as Edge, Electron apps such as VS Code, and VDE's own tabbed windows, the terminal and Explorer) is believed: it draws and manages its own frame and vwm adds no titlebar. Clients that ask for server-side, or say nothing, get the bar above.

Until a shell connects, windows get a plain bar that still moves, resizes and works. A shell that connects late
is asked to draw everything again.

Window menu (icon click, titlebar right-click, Alt+Space, or `menu N` over the socket): Restore, Move, Size,
Minimize, Maximize, Close. Double-clicking the icon closes; double-clicking the titlebar does what
`HKCU\Software\Vde\Windows\TitlebarDoubleClick` says (Maximize, Minimize, Close or None).

Window shadows, Windows 11 style: a soft black rectangle behind each managed window, blurred on the CPU and
cached per size in two strengths (focused `SHADOW_ALPHA_ACTIVE`, unfocused `SHADOW_ALPHA_INACTIVE` in
`native/vwm/vwm.c`). The shadow reaches `SHADOW_MARGIN` past the window and falls `SHADOW_DROP` downward.
It hides while the window is maximized, fullscreen, or not mapped, and it never intercepts the pointer
(`point_accepts_input` refuses every hit), so clicks pass through its margin band.

## Control socket

`$XDG_RUNTIME_DIR/vde/vwm.sock`, mode 0600. One JSON object per line out, one command per line in. The protocol
is documented at the top of `native/vwm/ipc.c`. `Vde.Compositor.VwmClient` is the C# side, and it is the
taskbar's `IWindowSource`, so the taskbar lists real windows when vwm runs.

## System tray

`Vde.Shell.Tray` (referenced by the taskbar host, `VExplore/TrayAreaController.cs`) implements the freedesktop
StatusNotifierItem protocol: the shell owns `org.kde.StatusNotifierWatcher` and `com.canonical.dbusmenu`, apps
register their `org.kde.StatusNotifierItem` objects, and the taskbar collects their icons next to the clock.
Icons re-read on the app's NewTitle/NewIcon/NewToolTip signals, clicks call Activate / SecondaryActivate, and
right-click opens the app's menu read (read-only) from its `com.canonical.dbusmenu` object. Items vanish when
their connection leaves the bus.

Programs that still dock through the old X11 protocol (`_NET_SYSTEM_TRAY_S0` + XEmbed: Steam, Wine, older GTK and
Qt apps) are handled by `vde-xembed` (`native/vxembed`, the session component `xembed-tray`). It owns the selection on
vwm's Xwayland, keeps each docked icon in a hidden 24x24 container, reads the picture back through XComposite (kept
current with Damage) and publishes it as a StatusNotifierItem, so the taskbar and any other StatusNotifierHost show it.
Left, middle and right clicks and scrolling go back to the program as button events; the item disappears when the
program closes or takes its window back. `native/vxembed/test-client.c` is a stand-in tray program for testing it.

## Not built yet

Snapping/tiling, virtual desktops, an Alt+Tab switcher overlay, multi-monitor layout policy, window
animations, screen capture/lock protocols, input method support, the X11 window-manager mode, and the session
glue (starting vwm, vexplore --chrome, the taskbar and the desktop together).
