# Installing VDE

## Arch Linux / CachyOS

    cd packaging/arch
    makepkg -d        # builds the package from this checkout (network needed once, for NuGet)
    sudo pacman -U vde-0.1.0-1-x86_64.pkg.tar.zst

`-d` skips the dependency check because the .NET SDK may be installed outside pacman; with everything from pacman
use `makepkg -s`. The package installs to `/usr`, creates the `vdedm` account (sysusers), builds the system
registry `/etc/hive/System.fhive` from `/usr/share/vde/registry/system.reg` if it does not exist, and registers the
shell's components in it. **It does not start anything or change how you log in.**

Dependencies: `dotnet-runtime-9.0` (the programs are built and tested against it; .NET 10 would also run them
through roll-forward), PAM, Wayland and its libraries, Xwayland, the Breeze icon theme, polkit.

### Trying it

1. **Inside your current desktop** (no risk): run `vde-session`. vwm opens as a window and the whole session runs
   in it. Point `XDG_CONFIG_HOME` at an empty folder to keep your own autostart programs out of it.
2. **As a login session**: choose "Vibe Desktop Environment" in your login manager (SDDM and GDM read
   `/usr/share/wayland-sessions/vde.desktop`), or run `start-vde` on a text console.
3. **With vdedm as the login manager**: see `dm.md`. Keep a text console available the first time.

## Plain `make install` (any distribution)

    git submodule update --init --recursive
    make                       # needs meson, ninja, a C compiler, the .NET 9 SDK, and the libraries listed above
    sudo make install PREFIX=/usr
    sudo systemd-sysusers vdedm.conf && sudo systemd-tmpfiles --create vdedm.conf
    sudo install -d /etc/hive
    sudo mkhive create --type system --rules /usr/share/vde/registry/permissions.rules \
         /etc/hive/System.fhive /usr/share/vde/registry/system.reg
    sudo RegSvrVDE --system /i /usr/lib/vde/vexplore.dll

`make uninstall` removes the files (not `/etc/hive`, nor anyone's settings).

## Layout

| Where | What |
| --- | --- |
| `/usr/lib/vde/` | every VDE program and library (`vwm`, `vterm-launch`, `vexplore`, `vde-session`, `vdedm`, ...) |
| `/usr/bin/` | `start-vde`, and links to `vexplore`, `vterminal`, `vde-session`, `vdedm`, `vdedm-greeter`, `mkhive`, `RegSvrVDE` |
| `/usr/share/vde/` | the default system registry (`registry/`), terminal integration scripts (`terminal/`) |
| `/etc/hive/System.fhive` | the machine's registry (made once; yours to edit) |
| `/etc/pam.d/vdedm*` | PAM services of the display manager (kept as configuration) |
| `/usr/lib/systemd/{system,user}` | `fhived`, `vdedm`, `vdenotify` units |
