#!/usr/bin/env bash
# Builds libwayland from the external/wayland submodule and installs it under
# artifacts/wayland, so the test suite can run against the version the bindings
# were generated from rather than whatever the distribution ships.
#
#   eng/build-wayland.sh          # build and install
#   eng/build-wayland.sh --test   # ... then run the test suite against it
#
# To use the result by hand:
#   LD_LIBRARY_PATH=$PWD/artifacts/wayland/lib dotnet test
#
# Nothing is written inside the submodule: the build tree lives in artifacts/,
# which is already gitignored.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/external/wayland"
BUILD_DIR="$REPO_ROOT/artifacts/wayland-build"
PREFIX="$REPO_ROOT/artifacts/wayland"

if [ ! -f "$SOURCE_DIR/meson.build" ]; then
    echo "external/wayland is empty; run: git submodule update --init" >&2
    exit 1
fi

for tool in meson ninja; do
    command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done

# The bindings only need the libraries; skipping the scanner's test suite and
# the documentation keeps the dependency set to libffi and expat.
if [ ! -d "$BUILD_DIR" ]; then
    meson setup "$BUILD_DIR" "$SOURCE_DIR" \
        --prefix="$PREFIX" \
        -Dtests=false \
        -Ddocumentation=false \
        -Ddtd_validation=false
fi

ninja -C "$BUILD_DIR" install

VERSION="$(sed -n "s/^\s*version: '\(.*\)',$/\1/p" "$SOURCE_DIR/meson.build" | head -1)"
echo
echo "libwayland $VERSION installed to $PREFIX"
echo "run tests against it with:"
echo "  LD_LIBRARY_PATH=$PREFIX/lib dotnet test"

if [ "${1:-}" = "--test" ]; then
    echo
    LD_LIBRARY_PATH="$PREFIX/lib" dotnet test "$REPO_ROOT" --nologo
fi
