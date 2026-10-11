# Vibe Desktop Environment

A C# desktop environment for Wayland built on Avalonia, with a Windows 11-style
look and workflow (original artwork, names and layout; no Microsoft assets or
trademarks). AI features are optional and off unless enabled.

## Layout

- `src/libFluent` — Fluent-style Avalonia control library (LGPL-3.0-or-later)
- `src/Vibe.Elekra` — Elekra, the registry (LGPL-3.0-or-later)
- `src/Vibe.Shell` — taskbar, launcher, notifications, desktop (GPL-3.0-or-later)
- compositor with X11 fallback and XWayland — later stage

## Licensing

Libraries are LGPL-3.0-or-later (`COPYING.LESSER`, with `COPYING`) so that
other Avalonia apps, including non-copyleft ones, can link them. The shell and
compositor executables are GPL-3.0-or-later (`COPYING`). Avalonia itself is MIT,
which is compatible with both.
