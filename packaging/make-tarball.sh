#!/bin/sh
# Makes vde-VERSION.tar.gz from the checkout, submodules included (git archive leaves them out), for rpmbuild and
# dpkg-buildpackage. usage: packaging/make-tarball.sh [VERSION] [OUTPUT-DIR]
set -e
cd "$(dirname "$0")/.."
version=${1:-0.1.0}
out=${2:-packaging}
git ls-files --recurse-submodules -z | tar --null -T - --transform "s,^,vde-$version/," -czf "$out/vde-$version.tar.gz"
echo "$out/vde-$version.tar.gz"
