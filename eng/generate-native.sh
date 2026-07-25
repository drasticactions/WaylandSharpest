#!/usr/bin/env bash
# Regenerates the committed ClangSharp bindings in src/WaylandSharpest/Native
# from the wayland submodule headers. Run after updating external/wayland.
#
# Requires the ClangSharpPInvokeGenerator dotnet tool:
#   dotnet tool install --global ClangSharpPInvokeGenerator
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WAYLAND_SRC="$REPO_ROOT/external/wayland/src"
OUT_DIR="$REPO_ROOT/src/WaylandSharpest/Native/generated"
GEN="${CLANGSHARP_GENERATOR:-ClangSharpPInvokeGenerator}"

# meson normally generates wayland-version.h; recreate it from the template.
WAYLAND_VERSION="$(sed -n "s/^\s*version: '\([0-9.]*\)',/\1/p" "$REPO_ROOT/external/wayland/meson.build" | head -1)"
IFS=. read -r VMAJOR VMINOR VMICRO <<<"$WAYLAND_VERSION"
INCLUDE_DIR="$(mktemp -d)"
trap 'rm -rf "$INCLUDE_DIR"' EXIT
sed -e "s/@WAYLAND_VERSION_MAJOR@/$VMAJOR/" \
    -e "s/@WAYLAND_VERSION_MINOR@/$VMINOR/" \
    -e "s/@WAYLAND_VERSION_MICRO@/$VMICRO/" \
    -e "s/@WAYLAND_VERSION@/$WAYLAND_VERSION/" \
    "$WAYLAND_SRC/wayland-version.h.in" > "$INCLUDE_DIR/wayland-version.h"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

RESOURCE_DIR="$(clang -print-resource-dir 2>/dev/null || true)"

COMMON_ARGS=(
    -x c -std c11
    -I "$WAYLAND_SRC" -I "$INCLUDE_DIR"
    -n Wayland.Native
    -c file=multi
    -g helper-types
    -lg exclusions
    -hf "$REPO_ROOT/eng/native-header.txt"
)
if [[ -n "$RESOURCE_DIR" ]]; then
    COMMON_ARGS+=(-rd "$RESOURCE_DIR")
fi

# Variadic functions cannot be P/Invoked portably on Linux; the runtime uses
# the *_array variants instead. Static-inline helpers that translate poorly
# (wl_fixed_* and the container_of-based wl_signal helpers) are reimplemented
# by hand in the runtime library.
CLIENT_EXCLUDES=(
    -e wl_proxy_marshal
    -e wl_proxy_marshal_flags
    -e wl_proxy_marshal_constructor
    -e wl_proxy_marshal_constructor_versioned
    -e wl_fixed_to_double -e wl_fixed_from_double
    -e wl_fixed_to_int -e wl_fixed_from_int
    -e wl_log_set_handler_client
)
# The server run only traverses wayland-server-core.h, but ClangSharp still
# re-emits referenced util types; exclude everything the client run already
# produced so the two runs compose into one namespace.
SERVER_EXCLUDES=(
    -e wl_resource_post_event
    -e wl_resource_queue_event
    -e wl_resource_post_error
    -e wl_client_post_implementation_error
    -e wl_signal_init -e wl_signal_add -e wl_signal_get -e wl_signal_emit
    -e wl_list -e wl_array -e wl_message -e wl_interface -e wl_argument
    -e wl_object -e wl_iterator_result
    -e wl_log_set_handler_server -e wl_resource_post_error_vargs
)

echo "== libwayland-client + util =="
"$GEN" "${COMMON_ARGS[@]}" \
    -f "$WAYLAND_SRC/wayland-client-core.h" \
    -t "$WAYLAND_SRC/wayland-client-core.h" -t "$WAYLAND_SRC/wayland-util.h" \
    -l wayland-client \
    -m LibWaylandClient \
    "${CLIENT_EXCLUDES[@]}" \
    -o "$OUT_DIR"

echo "== libwayland-server =="
"$GEN" "${COMMON_ARGS[@]}" \
    -f "$WAYLAND_SRC/wayland-server-core.h" \
    -t "$WAYLAND_SRC/wayland-server-core.h" \
    -l wayland-server \
    -m LibWaylandServer \
    "${SERVER_EXCLUDES[@]}" \
    -o "$OUT_DIR"

echo "Done. Output in $OUT_DIR"
