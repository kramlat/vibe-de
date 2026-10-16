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

## Which releases build

Built in clean containers (`packaging/test-deb.sh IMAGE`, and rpmbuild in a Fedora container):

| Distribution | Result |
| --- | --- |
| Arch / CachyOS | package builds (`makepkg -d`) |
| Fedora 44 | RPM builds |
| Debian 13 (trixie) | `.deb` builds, with `dpkg-shlibdeps` computing the library dependencies |
| Ubuntu 24.04 | **does not build**: the vendored wlroots needs Wayland 1.23.1, and 24.04 has 1.22.0 |
| Ubuntu 25.10 | see below |

.NET 9 is not in Debian's repositories, so the `.deb` build gets it from Microsoft's install script (the container script
does this); on Fedora and Arch it is a package. None of these packages has been installed on a real system yet.

## Package names by distribution

The programs need these libraries at run time; the package names differ. The Arch column is what `packaging/arch/PKGBUILD`
uses and is what the package was built against. **The Debian and Fedora columns are from memory and have not been
checked against a release**: names move between versions, so confirm with `apt-cache search` or `dnf provides`.

| Library | Arch / CachyOS | Debian / Ubuntu | Fedora |
| --- | --- | --- | --- |
| libseat | `seatd` | `libseat1` (build: `libseat-dev`) | `libseat` |
| Wayland | `wayland` | `libwayland-server0` | `wayland` |
| xkbcommon | `libxkbcommon` | `libxkbcommon0` | `libxkbcommon` |
| libinput | `libinput` | `libinput10` | `libinput` |
| libdrm | `libdrm` | `libdrm2` | `libdrm` |
| EGL, GLES, gbm | `mesa`, `libglvnd` | `libegl1`, `libgles2`, `libgbm1` | `mesa-libEGL`, `mesa-libGLES`, `mesa-libgbm` |
| pixman | `pixman` | `libpixman-1-0` | `pixman` |
| PAM | `pam` | `libpam0g` | `pam` |
| libdisplay-info | `libdisplay-info` | `libdisplay-info1` (recent releases only) | `libdisplay-info` |
| xcb-util wm / errors / renderutil | `xcb-util-wm`, `xcb-util-errors`, `xcb-util-renderutil` | `libxcb-ewmh2`, `libxcb-icccm4`, `libxcb-errors0`, `libxcb-render-util0` | `xcb-util-wm`, `xcb-util-errors`, `xcb-util-renderutil` |
| Xwayland | `xorg-xwayland` | `xwayland` | `xorg-x11-server-Xwayland` |
| Breeze icons | `breeze-icons` | `breeze-icon-theme` | `breeze-icon-theme` |
| polkit | `polkit` | `polkitd` | `polkit` |
| .NET 9 runtime | `dotnet-runtime-9.0` | Microsoft's package feed (or the distribution's, where it has 9) | `dotnet-runtime-9.0` |

For a `.deb`, do not write the library list by hand: `dpkg-shlibdeps` computes it from `vwm` and the other native
binaries, which is how Debian packages normally get names like `libseat1` right.
