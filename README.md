# Vibe Desktop Environment

A C# desktop environment for Wayland built on Avalonia, with a Windows 11-style
look and workflow (original artwork, names and layout; no Microsoft assets or
trademarks). AI features are optional and off unless enabled.

It has no telemetry.

## Layout

- `src/Vde.Freedesktop` — XDG dirs, .desktop entries, icon/sound/cursor theme lookup (LGPL-3.0-or-later)
- `src/Vde.Shell` — shell32-style services, no UI: known folders, MIME, file associations, launching, Trash, file operations, Computer/Network (LGPL-3.0-or-later)
- `src/Vde.Shell.Controls` — Avalonia controls for shell concepts: icon view, drive tile, usage bar (LGPL-3.0-or-later)
- `src/Vde.Shell.Theme` — theme engine: Fluent + Simple (light/dark/system) and user themes from `<data>/vde/themes/<id>/index.theme`, colour tokens, registry-backed settings (LGPL-3.0-or-later)
- `src/mkhive` — creates and fills hive files for installers and build scripts: .reg inputs, permission rules, reproducible output, never needs root (`mkhive --help`)
- `src/Vde.Components` + `src/RegSvrVDE` — component declaration ([VdeComponent]), COM-style registration under HKCR\CLSID, trust checks; RegSvrVDE is the regsvr32 equivalent (reads assemblies without running them) (LGPL-3.0-or-later)
- `src/Vibe.Registry.Remote` + `src/fhived` — the registry daemon: socket protocol (`docs/fhive-protocol.md`), RemoteHive client, HiveServer; permissions enforced against the kernel-reported identity; systemd units in `data/systemd` (LGPL-3.0-or-later)
- `src/libFluent` — Fluent-style Avalonia control library (LGPL-3.0-or-later)
- `src/Vibe.Registry` — the registry: typed hive files (`.fhive`, checksummed, atomic saves) with `.reg` import/export and change events (LGPL-3.0-or-later)
- `tests/` — xunit tests (`dotnet test`); `Vde.Policy.Tests` enforces the no-telemetry promise (no reporting packages, no internet APIs, Unix sockets only)
- `data/icons` — Vibe artwork in freedesktop icon-theme layout (`start-here-vde`: launcher mark + symbolic variant; original, LGPL-3.0-or-later)
- `src/Vibe.Shell` — taskbar, launcher, notifications, desktop (GPL-3.0-or-later)
- compositor with X11 fallback and XWayland — later stage

## Licensing

Libraries are LGPL-3.0-or-later (`COPYING.LESSER`, with `COPYING`) so that
other Avalonia apps, including non-copyleft ones, can link them. The shell and
compositor executables are GPL-3.0-or-later (`COPYING`). Avalonia itself is MIT,
which is compatible with both.
