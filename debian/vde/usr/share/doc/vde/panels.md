# Panels on Wayland: findings from the layer-shell spike

**Question:** can VDE's panels (taskbar, launcher popup, toasts) be Avalonia windows that sit at a screen edge?

**Finding:** not directly, but yes with a small native client. Verified on KWin (Plasma, Wayland) on 2026-10-16.

- Avalonia 11.3 has no Wayland backend. Its windows run through XWayland, so they are X11 windows and cannot
  be `wlr-layer-shell` surfaces.
- KWin offers `zwlr_layer_shell_v1` (version 5); labwc, Sway and Hyprland do too. GNOME's compositor does not.
- So panels are **drawn offscreen by Avalonia and shown by a native layer-shell client**:
  `native/vdepanel` (C, about 150 lines, builds with `native/vdepanel/build.sh`) takes a BGRA image, creates a
  layer surface anchored to an edge and attaches the pixels through `wl_shm`.
- `tools/PanelSpike` proves the whole path: real VDE controls rendered with Avalonia's offscreen Skia, converted
  to premultiplied BGRA, shown at the top edge for a few seconds. KWin accepted it with no protocol error.

## What the spike did not cover (the real work)

- **Input:** pointer, keyboard and touch events must come back from the Wayland surface and be injected into
  Avalonia (the offscreen window needs `RaiseEvent`/headless input forwarding).
- **Live updates:** a loop that re-renders when something changes (clock tick, tray icon, hover) and re-attaches
  buffers, using `wl_surface.frame` callbacks to pace it and double-buffering to avoid tearing.
- **Transparency:** `CaptureRenderedFrame` returns an opaque bitmap; panels with rounded corners need a render
  target with alpha (`RenderTargetBitmap`).
- **HiDPI and outputs:** `wl_output` scale and geometry, one panel per monitor, hotplug.
- **Popups:** the launcher and flyouts are separate layer surfaces (or `xdg_popup`s via `get_popup`) positioned
  against the taskbar.
- **Struts:** `exclusive_zone` makes maximised windows stop at the panel; the spike deliberately used 0.
- **Compositors without layer-shell** (GNOME): fall back to an ordinary X11 dock window under XWayland, or run
  VDE's own compositor (stage 3 of the plan).

## Recommendation

Proceed with the native-client design. The shim should grow into a small library with a callback interface
(`configure`, `pointer`, `key`, `frame`, `closed`) that a C# `PanelHost` wraps, so each panel is just an Avalonia
control plus an anchor. Fedora and Arch both ship `libwayland-client`; the install target builds the shim with
the rest.
