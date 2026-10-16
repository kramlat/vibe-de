#!/bin/sh
# Builds wlroots (vendored, static, into native/build/prefix) and then vwm into native/build/vwm.
# Needs: meson ninja gcc pkg-config wayland-scanner and the dev files of wayland, pixman, libxkbcommon,
# libinput, libdrm, libseat, EGL/GLES2, gbm, xcb(+composite,render,xfixes,icccm,ewmh), libudev, libdisplay-info.
set -e
here=$(cd "$(dirname "$0")" && pwd)
native=$(dirname "$here")
prefix=$native/build/prefix
export PKG_CONFIG_PATH=$prefix/share/pkgconfig:$prefix/lib/pkgconfig:$prefix/lib64/pkgconfig:$PKG_CONFIG_PATH

# the vendored sources: present in a release tarball, fetched with git in a checkout that has not got them yet
if [ ! -f "$native/third_party/wlroots/meson.build" ] || [ ! -f "$native/third_party/wayland-protocols/meson.build" ]; then
  git -C "$native/.." submodule update --init native/third_party/wayland-protocols native/third_party/wlroots
fi
if [ ! -f "$prefix/share/pkgconfig/wayland-protocols.pc" ]; then
  meson setup "$native/build/wp" "$native/third_party/wayland-protocols" --prefix="$prefix" -Dtests=false >/dev/null
  ninja -C "$native/build/wp" install >/dev/null
fi
if [ ! -f "$prefix/lib/pkgconfig/wlroots-0.19.pc" ]; then
  meson setup "$native/build/wlr" "$native/third_party/wlroots" --prefix="$prefix" -Ddefault_library=static -Dexamples=false \
    -Dxwayland=enabled -Drenderers=gles2 -Dbackends=drm,libinput,x11 -Dallocators=gbm -Dcolor-management=disabled -Dsession=enabled >/dev/null
  ninja -C "$native/build/wlr" install >/dev/null
fi

out=$native/build/vwm
mkdir -p "$out"
scanner=$(pkg-config --variable=wayland_scanner wayland-scanner)
proto=$(pkg-config --variable=pkgdatadir wayland-protocols)
for x in xdg-shell; do
  "$scanner" server-header "$proto/stable/$x/$x.xml" "$out/$x-protocol.h"
done
"$scanner" server-header "$here/../vdepanel/protocols/wlr-layer-shell-unstable-v1.xml" "$out/wlr-layer-shell-unstable-v1-protocol.h"
${CC:-cc} -std=gnu11 -Wall -Wextra -Wno-unused-parameter -DWLR_USE_UNSTABLE -I"$out" -I"$here" \
  "$here/vwm.c" "$here/ipc.c" "$here/json.c" -o "$out/vwm" \
  $(pkg-config --cflags --libs wlroots-0.19 wayland-server xkbcommon) -lm
echo "built $out/vwm"
