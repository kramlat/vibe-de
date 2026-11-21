#!/bin/sh
# Builds vde-xembed into native/build/vxembed/. Needs a C compiler and the dev files of xcb (+composite, damage) and libsystemd.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=$here/../build/vxembed
mkdir -p "$out"
${CC:-cc} -std=gnu11 -Wall -Wextra -Wno-unused-parameter -O2 "$here/vxembed.c" -o "$out/vde-xembed" \
  $(pkg-config --cflags --libs xcb xcb-composite xcb-damage libsystemd)
echo "built $out/vde-xembed"
