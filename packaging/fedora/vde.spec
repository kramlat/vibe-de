Name:           vde
Version:        0.1.0
Release:        1%{?dist}
Summary:        Vibe Desktop Environment: a C# / Avalonia desktop for Wayland
License:        GPL-3.0-or-later AND LGPL-3.0-or-later
URL:            https://github.com/kramlat/vibe-de
# packaging/make-tarball.sh: the checkout with its submodules (wlroots is vendored and linked statically)
Source0:        vde-%{version}.tar.gz

# nothing here is ELF code worth stripping or debuginfo-splitting: the .NET programs are managed assemblies
%global debug_package %{nil}
%global __os_install_post %{nil}

BuildRequires:  dotnet-sdk-9.0
BuildRequires:  gcc
BuildRequires:  make
BuildRequires:  meson
BuildRequires:  ninja-build
BuildRequires:  hwdata
BuildRequires:  systemd-rpm-macros
BuildRequires:  pkgconfig(wayland-server)
BuildRequires:  pkgconfig(wayland-protocols)
BuildRequires:  pkgconfig(xkbcommon)
BuildRequires:  pkgconfig(libinput)
BuildRequires:  pkgconfig(libdrm)
BuildRequires:  pkgconfig(gbm)
BuildRequires:  pkgconfig(egl)
BuildRequires:  pkgconfig(glesv2)
BuildRequires:  pkgconfig(pixman-1)
BuildRequires:  pkgconfig(libseat)
BuildRequires:  pkgconfig(libudev)
BuildRequires:  pkgconfig(xcb)
BuildRequires:  pkgconfig(xcb-composite)
BuildRequires:  pkgconfig(xcb-render)
BuildRequires:  pkgconfig(xcb-xfixes)
BuildRequires:  pkgconfig(xcb-icccm)
BuildRequires:  pkgconfig(xcb-ewmh)
BuildRequires:  pkgconfig(xcb-errors)
BuildRequires:  pkgconfig(xcb-renderutil)
BuildRequires:  pkgconfig(libdisplay-info)

# libraries linked by the native programs are found by rpm itself; these are loaded at run time or are other programs
Requires:       dotnet-runtime-9.0
Requires:       pam
Requires:       xorg-x11-server-Xwayland
Requires:       breeze-icon-theme
Requires:       polkit
Requires:       dbus-common
%{?systemd_requires}

%description
VDE is a desktop environment for Wayland written in C# on the Avalonia toolkit, with a Windows 11 style workflow
(original artwork and names). It includes the vwm compositor, a shell (taskbar, desktop, launcher, settings, file
manager), a terminal with PowerShell and bash integration, a session manager, a display manager (vdedm), a
notification daemon and a registry-based configuration store. It has no telemetry.

%prep
%autosetup -n vde-%{version}

%build
make DOTNET=dotnet CONFIG=Release all

%install
make DESTDIR=%{buildroot} PREFIX=%{_prefix} UNITDIR=%{_prefix}/lib/systemd install

%pre
%sysusers_create_compat data/sysusers.d/vdedm.conf 2>/dev/null || :

%post
systemd-sysusers vdedm.conf >/dev/null 2>&1 || :
systemd-tmpfiles --create vdedm.conf >/dev/null 2>&1 || :
if [ ! -e /etc/hive/System.fhive ]; then
    install -d -m755 /etc/hive
    %{_prefix}/lib/vde/mkhive create --type system --rules %{_datadir}/vde/registry/permissions.rules \
        /etc/hive/System.fhive %{_datadir}/vde/registry/system.reg >/dev/null || :
fi
%{_prefix}/lib/vde/RegSvrVDE --system /s /i %{_prefix}/lib/vde/vexplore.dll >/dev/null 2>&1 || :

%preun
if [ $1 -eq 0 ]; then
    %{_prefix}/lib/vde/RegSvrVDE --system /s /u %{_prefix}/lib/vde/vexplore.dll >/dev/null 2>&1 || :
fi

%files
%license COPYING COPYING.LESSER
%doc %{_datadir}/doc/vde
%{_prefix}/lib/vde
%{_bindir}/start-vde
%{_bindir}/vexplore
%{_bindir}/vterminal
%{_bindir}/vde-session
%{_bindir}/vdedm
%{_bindir}/vdedm-greeter
%{_bindir}/mkhive
%{_bindir}/RegSvrVDE
%{_bindir}/regsvrvde
%{_datadir}/vde
%{_datadir}/icons/hicolor/scalable/places/start-here-vde.svg
%{_datadir}/icons/hicolor/symbolic/places/start-here-vde-symbolic.svg
%{_datadir}/wayland-sessions/vde.desktop
%{_datadir}/applications/vexplore.desktop
%{_datadir}/applications/vexplore-settings.desktop
%{_datadir}/applications/vterminal.desktop
%{_datadir}/dbus-1/services/org.freedesktop.Notifications.service
%{_datadir}/polkit-1/rules.d/50-vdedm-greeter.rules
%{_prefix}/lib/systemd/system/fhived-system.service
%{_prefix}/lib/systemd/system/fhived-system.socket
%{_prefix}/lib/systemd/system/vdedm.service
%{_prefix}/lib/systemd/user/fhived.service
%{_prefix}/lib/systemd/user/fhived.socket
%{_prefix}/lib/systemd/user/vdenotify.service
%{_prefix}/lib/sysusers.d/vdedm.conf
%{_prefix}/lib/tmpfiles.d/vdedm.conf
%config(noreplace) %{_sysconfdir}/pam.d/vdedm
%config(noreplace) %{_sysconfdir}/pam.d/vdedm-greeter
%config(noreplace) %{_sysconfdir}/pam.d/vdedm-autologin

%changelog
* Fri Oct 16 2026 Mark Toman <kram2048@gmail.com> - 0.1.0-1
- First packaging
