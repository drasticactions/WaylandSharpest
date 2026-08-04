#!/usr/bin/env bash
# Builds libwayland from the external/wayland submodule and installs it under
# artifacts/, so the test suite can run against a known version rather than
# whatever the distribution happens to ship.
#
#   eng/build-wayland.sh                    # the submodule as checked out
#   eng/build-wayland.sh --test             # ... then run the suite against it
#   eng/build-wayland.sh --version 1.22.0   # a specific tag, for the floor
#
# To use a result by hand:
#   LD_LIBRARY_PATH=$PWD/artifacts/wayland/lib dotnet test
#
# Nothing is written inside the submodule: build trees and checkouts live in
# artifacts/, which is already gitignored.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SUBMODULE="$REPO_ROOT/external/wayland"

RUN_TESTS=0
TAG=""
while [ $# -gt 0 ]; do
    case "$1" in
        --test) RUN_TESTS=1; shift ;;
        --version) TAG="${2:?--version needs a tag}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done

if [ ! -f "$SUBMODULE/meson.build" ]; then
    echo "external/wayland is empty; run: git submodule update --init" >&2
    exit 1
fi

for tool in meson ninja; do
    command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done

if [ -n "$TAG" ]; then
    # A separate checkout rather than a submodule checkout: switching the
    # submodule would leave the parent repo pointing at the wrong commit.
    SOURCE_DIR="$REPO_ROOT/artifacts/wayland-$TAG-src"
    BUILD_DIR="$REPO_ROOT/artifacts/wayland-$TAG-build"
    PREFIX="$REPO_ROOT/artifacts/wayland-$TAG"
    if [ ! -d "$SOURCE_DIR" ]; then
        if git -C "$SUBMODULE" rev-parse --quiet --verify "refs/tags/$TAG^{commit}" >/dev/null 2>&1; then
            git clone --quiet --shared --no-checkout "$SUBMODULE" "$SOURCE_DIR"
            git -C "$SOURCE_DIR" checkout --quiet "$TAG"
        else
            # A shallow submodule has no tags, which is how actions/checkout
            # leaves it by default, so take just this one from upstream.
            url="$(git -C "$SUBMODULE" remote get-url origin)"
            echo "tag $TAG is not in the submodule; fetching it from $url"
            git clone --quiet --depth 1 --branch "$TAG" "$url" "$SOURCE_DIR"
        fi
    fi
else
    SOURCE_DIR="$SUBMODULE"
    BUILD_DIR="$REPO_ROOT/artifacts/wayland-build"
    PREFIX="$REPO_ROOT/artifacts/wayland"
fi

# The bindings only need the libraries; skipping the scanner's test suite and
# the documentation keeps the dependency set to libffi and expat.
#
# --libdir=lib is not cosmetic: meson defaults it per distribution, and on
# Debian derivatives that is the multiarch lib/x86_64-linux-gnu. Callers point
# LD_LIBRARY_PATH at $PREFIX/lib, and a mismatch there does not fail -- the
# loader just falls back to the system libwayland and the run silently tests
# the wrong library.
if [ ! -d "$BUILD_DIR" ]; then
    meson setup "$BUILD_DIR" "$SOURCE_DIR" \
        --prefix="$PREFIX" \
        --libdir=lib \
        -Dtests=false \
        -Ddocumentation=false \
        -Ddtd_validation=false
fi

ninja -C "$BUILD_DIR" install

for lib in client server cursor egl; do
    case "$lib" in
        egl) soname="libwayland-egl.so.1" ;;
        *)   soname="libwayland-$lib.so.0" ;;
    esac
    if [ ! -e "$PREFIX/lib/$soname" ]; then
        echo "expected $PREFIX/lib/$soname after install, but it is not there;" >&2
        echo "LD_LIBRARY_PATH=$PREFIX/lib would silently fall back to the system libwayland." >&2
        exit 1
    fi
done

VERSION="$(sed -n "s/^\s*version: '\(.*\)',$/\1/p" "$SOURCE_DIR/meson.build" | head -1)"
echo
echo "libwayland $VERSION installed to $PREFIX"
echo "run tests against it with:"
echo "  LD_LIBRARY_PATH=$PREFIX/lib dotnet test"

if [ "$RUN_TESTS" -eq 1 ]; then
    echo
    LD_LIBRARY_PATH="$PREFIX/lib" dotnet test "$REPO_ROOT" --nologo
fi
