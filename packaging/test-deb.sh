#!/bin/sh
# Builds the .deb in a clean Debian or Ubuntu container (podman or docker) and shows what came out.
#   packaging/test-deb.sh [IMAGE] [OUTPUT-DIR]       e.g.  packaging/test-deb.sh docker.io/library/ubuntu:25.10
# .NET 9 is not in Debian's repositories (and in few Ubuntu releases'), so the container gets it from Microsoft's
# install script; the control file's dotnet-sdk-9.0 build dependency is dropped inside the container for that reason.
set -e
cd "$(dirname "$0")/.."
image=${1:-docker.io/library/debian:trixie}
out=${2:-/tmp/vde-deb}
engine=$(command -v podman || command -v docker)
mkdir -p "$out"
tarball=$(packaging/make-tarball.sh 0.1.0 /tmp)
"$engine" run --rm -v "$tarball:/src/vde.tar.gz:ro" -v "$out:/out:Z" "$image" sh -ec '
    export DEBIAN_FRONTEND=noninteractive
    cat /etc/os-release | head -2
    apt-get update -qq
    apt-get install -y -qq build-essential debhelper devscripts equivs wget ca-certificates git libicu-dev >/dev/null
    mkdir /build && tar -xzf /src/vde.tar.gz -C /build && cd /build/vde-0.1.0
    sed -i "s/ dotnet-sdk-9.0,//" debian/control
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet >/dev/null
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
    mk-build-deps -ir -t "apt-get -y -qq --no-install-recommends" debian/control >/dev/null
    dpkg-buildpackage -us -uc -b
    cp ../*.deb /out/
    dpkg-deb -I /out/vde_*.deb | grep -E "Depends|Package|Version|Installed"
    dpkg-deb -c /out/vde_*.deb | grep -E "bin/|vwm|systemd" | head -14
'
