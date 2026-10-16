# The VDE session

A login manager (or you, from a text console) starts the session; the session manager keeps it running.

```
login manager ──reads── /usr/share/wayland-sessions/vde.desktop ──Exec──▶ start-vde ──exec──▶ vde-session
```

- **`data/wayland-sessions/vde.desktop`** is the session file (`DesktopNames=VDE`, so `XDG_CURRENT_DESKTOP=VDE`).
- **`data/bin/start-vde`** is the script it runs, and the one to run by hand from a TTY. It refuses root, makes sure
  there is an `XDG_RUNTIME_DIR` and a session bus (`dbus-run-session` if there is none), sources
  `~/.config/vde/environment.sh` if present, writes the session's output to `~/.local/state/vde/session.log`
  (the previous one is kept as `.old`), then replaces itself with `vde-session`.
- **`vde-session`** (`src/VDESession`) is the session manager. Install it next to the other VDE programs
  (`/usr/lib/vde`, or anywhere in `VDE_LIBEXEC`).

## What vde-session does

1. Sets the session environment: `XDG_CURRENT_DESKTOP=VDE`, `XDG_SESSION_TYPE=wayland`, toolkit hints (Qt, GTK,
   Firefox, SDL, Electron prefer Wayland and fall back to X11).
2. Starts the compositor, `vwm -w wayland-vde`, and waits for it to report its displays
   (`$XDG_RUNTIME_DIR/vde/vwm.env`: `WAYLAND_DISPLAY`, then `DISPLAY` once XWayland is up; a session without
   XWayland still starts after a few seconds).
3. Tells systemd's user manager and D-Bus activation about the display, so services they start find it.
4. Starts, in order, with that display in their environment: `vexplore --chrome` (window titlebars and menus),
   `vexplore --desktop` (wallpaper and icons), `vexplore --taskbar`, `VDENotify`.
5. Starts the autostart programs: `.desktop` files in `~/.config/autostart` and `/etc/xdg/autostart`, with the
   usual rules (`OnlyShowIn`/`NotShowIn`, `Hidden`, `TryExec`); a user file of the same name overrides the system's,
   which is how a user switches one off.
6. Keeps them running. A program that crashes is restarted after a second; one that dies more than five times in
   thirty seconds is given up on (the others carry on). `VDENotify` exiting with status 2 (another notification
   daemon owns the name) is not retried. If **vwm ends, the session ends** (status 1).
7. On logout (`vde-session --logout`, SIGTERM/SIGHUP/SIGINT) stops autostart programs and the shell programs
   (SIGTERM, then SIGKILL after three seconds), and the compositor last.

## Changing it

`HKCU\Software\Vde\Session\Components\<name>` with `Enabled` (DWORD, 0 turns it off) and `Command` (a command line)
for `chrome`, `desktop`, `taskbar` and `notifications`. The compositor cannot be switched off or replaced. Replace
the taskbar with something else, or run without a desktop, by changing these values.

## Control

`vde-session --status` lists the programs and their state; `--logout` ends the session; `--dry-run` prints what
would start. The socket is `$XDG_RUNTIME_DIR/vde/session.sock` (mode 0600); `restart NAME` restarts one program.
A second `vde-session` refuses to start while one answers on that socket.

## Trying it without logging out

Run `vde-session` from a terminal inside your existing Wayland or X11 session: vwm opens as a window and the whole
session runs inside it. Point `XDG_CONFIG_HOME` somewhere empty to keep your own autostart programs out of it.
