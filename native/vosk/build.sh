#!/bin/sh
# Builds vde-osk (VDE's on-screen keyboard) into native/build/vosk/.
# Needs: a C compiler, wayland-scanner, and the dev files of wayland-client, cairo, pangocairo, xkbcommon.
# Protocol XML comes from the vendored wlroots / wayland-protocols submodules and native/vosk/protocol.
set -e
here=$(cd "$(dirname "$0")" && pwd)
native=$(dirname "$here")
out=$native/build/vosk
gen=$out/gen
mkdir -p "$gen"

wlr=$native/third_party/wlroots/protocol
wp=$native/third_party/wayland-protocols
scan() { # name xml
  wayland-scanner client-header "$2" "$gen/$1-client-protocol.h"
  wayland-scanner private-code "$2" "$gen/$1-protocol.c"
}
scan xdg-shell "$wp/stable/xdg-shell/xdg-shell.xml"
scan wlr-layer-shell "$wlr/wlr-layer-shell-unstable-v1.xml"
scan input-method-v2 "$wlr/input-method-unstable-v2.xml"
scan virtual-keyboard "$wlr/virtual-keyboard-unstable-v1.xml"
scan input-method-v1 "$here/protocol/input-method-unstable-v1.xml"

${CC:-cc} -std=gnu11 -Wall -Wextra -Wno-unused-parameter -O2 -I"$gen" "$here/vosk.c" "$gen"/*-protocol.c -o "$out/vde-osk" \
  $(pkg-config --cflags --libs wayland-client cairo pangocairo xkbcommon) -lm
echo "built $out/vde-osk"
