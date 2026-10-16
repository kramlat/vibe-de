#!/bin/sh
# Builds libvdepanel.so. Needs gcc, libwayland-client and wayland-scanner. (The install target will do this
# through the real build system; this script is for working on the panels.)
set -eu
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/build}
mkdir -p "$out"
for p in xdg-shell wlr-layer-shell-unstable-v1; do
    wayland-scanner client-header "$here/protocols/$p.xml" "$out/$p-client-protocol.h"
    wayland-scanner private-code  "$here/protocols/$p.xml" "$out/$p-protocol.c"
done
gcc -O2 -Wall -Wextra -fPIC -shared -I"$out" -o "$out/libvdepanel.so" "$here/vdepanel.c" \
    "$out/xdg-shell-protocol.c" "$out/wlr-layer-shell-unstable-v1-protocol.c" -lwayland-client
echo "$out/libvdepanel.so"
