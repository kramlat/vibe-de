# Builds and installs VDE.
#
#   make                       build everything (needs git submodules, meson, ninja, a C compiler, the .NET 9 SDK)
#   make install               install to PREFIX (default /usr); DESTDIR for staging (packages use it)
#   make uninstall             remove what install put in place (not the system registry or your settings)
#
# The system registry (/etc/hive/System.fhive) and the component registrations are not made here: they are a
# per-machine step the package's install script (or you, see docs/install.md) performs after `make install`.

PREFIX      ?= /usr
LIBDIR      ?= $(PREFIX)/lib/vde
DATADIR     ?= $(PREFIX)/share
SYSCONFDIR  ?= /etc
UNITDIR     ?= $(PREFIX)/lib/systemd
DOTNET      ?= dotnet
CONFIG      ?= Release
RID         ?= linux-x64
BUILD       := build
PUBLISH     := $(BUILD)/publish

# project directory under src/ -> published executable name (see each .csproj AssemblyName)
DOTNET_PROJECTS := fhived mkhive RegSvrVDE VDENotify VExplore VTerminal VDESession VDEDM VDEDMGreeter

# commands people type, linked into PREFIX/bin
BIN_LINKS := vexplore vterminal vde-session vdedm vdedm-greeter mkhive RegSvrVDE

export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1

.PHONY: all native dotnet install uninstall clean check
all: native dotnet

native:
	native/vwm/build.sh
	native/vterm/build.sh

dotnet:
	@mkdir -p $(PUBLISH)
	@for p in $(DOTNET_PROJECTS); do \
	    echo "publish $$p"; \
	    $(DOTNET) publish src/$$p -c $(CONFIG) -o $(PUBLISH) --nologo -v q \
	        -r $(RID) --self-contained false -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=true || exit 1; \
	done

install:
	install -d $(DESTDIR)$(LIBDIR)
	cp -a $(PUBLISH)/. $(DESTDIR)$(LIBDIR)/
	install -m755 native/build/vwm/vwm native/build/vterm/vterm-launch $(DESTDIR)$(LIBDIR)/
	install -d $(DESTDIR)$(PREFIX)/bin
	install -m755 data/session/start-vde $(DESTDIR)$(PREFIX)/bin/start-vde
	for b in $(BIN_LINKS); do ln -sf $(LIBDIR)/$$b $(DESTDIR)$(PREFIX)/bin/$$b; done
	ln -sf $(LIBDIR)/RegSvrVDE $(DESTDIR)$(PREFIX)/bin/regsvrvde
	# data
	install -d $(DESTDIR)$(DATADIR)/vde/terminal $(DESTDIR)$(DATADIR)/vde/registry
	install -m644 data/terminal/* $(DESTDIR)$(DATADIR)/vde/terminal/
	install -m644 data/registry/system.reg data/registry/permissions.rules $(DESTDIR)$(DATADIR)/vde/registry/
	install -d $(DESTDIR)$(DATADIR)/icons
	cp -a data/icons/hicolor $(DESTDIR)$(DATADIR)/icons/
	install -Dm644 data/wayland-sessions/vde.desktop $(DESTDIR)$(DATADIR)/wayland-sessions/vde.desktop
	install -d $(DESTDIR)$(DATADIR)/applications
	install -m644 data/applications/*.desktop $(DESTDIR)$(DATADIR)/applications/
	install -Dm644 data/dbus/org.freedesktop.Notifications.service $(DESTDIR)$(DATADIR)/dbus-1/services/org.freedesktop.Notifications.service
	# systemd: the registry daemons and the display manager are system units, the user's registry and notifications user units
	install -d $(DESTDIR)$(UNITDIR)/system $(DESTDIR)$(UNITDIR)/user
	install -m644 data/systemd/fhived-system.service data/systemd/fhived-system.socket data/systemd/vdedm.service $(DESTDIR)$(UNITDIR)/system/
	install -m644 data/systemd/fhived.service data/systemd/fhived.socket data/systemd/vdenotify.service $(DESTDIR)$(UNITDIR)/user/
	install -Dm644 data/sysusers.d/vdedm.conf $(DESTDIR)$(PREFIX)/lib/sysusers.d/vdedm.conf
	install -Dm644 data/tmpfiles.d/vdedm.conf $(DESTDIR)$(PREFIX)/lib/tmpfiles.d/vdedm.conf
	install -Dm644 data/polkit/50-vdedm-greeter.rules $(DESTDIR)$(DATADIR)/polkit-1/rules.d/50-vdedm-greeter.rules
	# PAM services (configuration: the administrator may edit them)
	install -d $(DESTDIR)$(SYSCONFDIR)/pam.d
	install -m644 data/pam.d/vdedm data/pam.d/vdedm-greeter data/pam.d/vdedm-autologin $(DESTDIR)$(SYSCONFDIR)/pam.d/
	install -d $(DESTDIR)$(DATADIR)/doc/vde $(DESTDIR)$(DATADIR)/licenses/vde
	install -m644 README.md docs/*.md $(DESTDIR)$(DATADIR)/doc/vde/
	install -m644 COPYING COPYING.LESSER $(DESTDIR)$(DATADIR)/licenses/vde/

uninstall:
	rm -rf $(DESTDIR)$(LIBDIR) $(DESTDIR)$(DATADIR)/vde $(DESTDIR)$(DATADIR)/doc/vde $(DESTDIR)$(DATADIR)/licenses/vde
	rm -f $(DESTDIR)$(PREFIX)/bin/start-vde $(DESTDIR)$(PREFIX)/bin/regsvrvde $(addprefix $(DESTDIR)$(PREFIX)/bin/,$(BIN_LINKS))
	rm -f $(DESTDIR)$(DATADIR)/wayland-sessions/vde.desktop $(DESTDIR)$(DATADIR)/applications/vexplore.desktop \
	      $(DESTDIR)$(DATADIR)/applications/vexplore-settings.desktop $(DESTDIR)$(DATADIR)/applications/vterminal.desktop \
	      $(DESTDIR)$(DATADIR)/dbus-1/services/org.freedesktop.Notifications.service
	rm -f $(DESTDIR)$(UNITDIR)/system/fhived-system.* $(DESTDIR)$(UNITDIR)/system/vdedm.service $(DESTDIR)$(UNITDIR)/user/fhived.* $(DESTDIR)$(UNITDIR)/user/vdenotify.service
	rm -f $(DESTDIR)$(PREFIX)/lib/sysusers.d/vdedm.conf $(DESTDIR)$(PREFIX)/lib/tmpfiles.d/vdedm.conf $(DESTDIR)$(DATADIR)/polkit-1/rules.d/50-vdedm-greeter.rules

# What the whole test suite says; the packaging step does not run it (it needs a display for some tests).
check:
	$(DOTNET) test --nologo -v q

clean:
	rm -rf $(BUILD) native/build
