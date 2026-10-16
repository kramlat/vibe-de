# vdedm: the VDE display manager

The login screen and the thing behind it. Not tried yet as the real thing: see "What has and has not been tested".

```
systemd ─▶ vdedm (root) ──starts──▶ vwm + vdedm-greeter   (as the account "vdedm", class greeter)
              │  ▲                          │ greetd protocol over /run/vdedm/greeter.sock
              │  └──────────────────────────┘
              └─ PAM (service vdedm) ─ logged in ─▶ start-vde (as the user, on the same terminal) ─▶ vde-session
```

- **`vdedm`** (src/VDEDM, logic in src/Vde.DisplayManager) runs as root from `vdedm.service`. It switches to the
  configured virtual terminal, starts the greeter, authenticates through **PAM**, opens the user's PAM session
  (so logind and `pam_systemd` create the session and `XDG_RUNTIME_DIR`), gives the terminal to the user, and starts
  their desktop through a login shell. When the desktop ends it closes the PAM session and shows the greeter again.
- **`vdedm-greeter`** (src/VDEDMGreeter) is the screen: clock, users, password, session choice, restart and shut
  down. Its logic (`GreeterModel`) is separate from its looks. It takes its theme and wallpaper from the system
  registry (HKLM), since nobody is logged in. `vdedm-greeter --demo` shows it in a window, password `demo`.
- **The protocol is greetd's** (length-prefixed JSON: `create_session`, `post_auth_message_response`,
  `start_session`, `cancel_session`), so greeters written for greetd (regreet, tuigreet, gtkgreet) work with vdedm.
  `VDEDM_SOCK` and `GREETD_SOCK` both name the socket.
- **Passwords** are checked by PAM and nothing else; fingerprint readers, lockouts after failures and expired
  accounts are whatever PAM is configured to do. After a failed attempt the next one waits (1, 2, 4... up to 16 s).
- **Autologin**: set `AutoLoginUser` (and optionally `AutoLoginSession`); it applies once after boot, not after a logout.

## Settings (HKLM\Software\Vde\DisplayManager)

| Value | Default | |
| --- | --- | --- |
| `Enabled` | 1 | 0 turns vdedm off |
| `Vt` | 1 | the virtual terminal it uses |
| `GreeterUser` | `vdedm` | the unprivileged account the login screen runs as |
| `GreeterCommand` | `vwm -d -w wayland-vdedm -s vdedm-greeter` | what shows the login screen |
| `DefaultSession` | `vde` | the session preselected |
| `AutoLoginUser`, `AutoLoginSession`, `AutoLoginOnce` | off | |

The greeter's wallpaper and theme are the usual `Software\Vde\Desktop` and `Software\Vde\Appearance` keys of the
system hive.

## Installing

`vdedm --check` reports what is missing. The files (all under `data/`):

- `systemd/vdedm.service` (takes over tty1 from getty; `systemctl enable vdedm`), `sysusers.d/vdedm.conf` and
  `tmpfiles.d/vdedm.conf` (the `vdedm` account and its folders)
- `pam.d/vdedm`, `vdedm-greeter`, `vdedm-autologin` → `/etc/pam.d` (the first includes `system-local-login`, which
  is what Arch and Fedora call the stack for local logins; on Debian include `common-auth` and friends)
- `polkit/50-vdedm-greeter.rules` lets the greeter's account use the shut down and restart buttons
- `wayland-sessions/vde.desktop` (the session), and `vwm`, `vterm-launch`, `vdedm-greeter`, `start-vde` and
  `vde-session` installed where `vdedm` finds them (`/usr/lib/vde`, or `VDE_LIBEXEC`)

Only Wayland sessions can be chosen. X11 sessions are listed by `--check` but need an X server launcher (not built).

## What has and has not been tested

Tested (without root): the protocol; real PAM through its own service files (permit, deny, passwords asked through
the conversation, abandoning a prompt); accounts and sessions discovery; the login rules (failure delays, input
checks, cancel); the daemon's whole cycle against a fake system, including the real socket (greeter → login →
session → logout → greeter, autologin, a wrong password, a greeter that dies); the greeter model and window.

**Not tested: anything that needs root and a real seat**: switching virtual terminals, handing a terminal to a
user, starting a process as another user, `pam_systemd`, vwm taking the screen from a bare VT, and polkit. Treat
the first run as a test: keep a way to reach a text console (Ctrl+Alt+F2) or `systemctl disable vdedm` from one.
