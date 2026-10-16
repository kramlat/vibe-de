#!/bin/sh
# Builds vterm-launch into native/build/vterm/. Needs only a C compiler.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=$here/../build/vterm
mkdir -p "$out"
${CC:-cc} -std=gnu11 -Wall -Wextra -O2 "$here/vterm-launch.c" -o "$out/vterm-launch"
echo "built $out/vterm-launch"
