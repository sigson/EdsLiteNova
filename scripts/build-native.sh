#!/usr/bin/env bash
#
# build-native.sh - Build the edscrypto native library and stage it into the
# Eds.Core runtimes folder so the managed build picks it up automatically.
#
# Works on Linux, macOS and Windows/msys2 (uses gcc or clang, whichever cmake
# finds). For Android/iOS cross builds see docs/BUILDING.md.
#
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NATIVE="$ROOT/native"
BUILD="$NATIVE/build"

# Detect RID and library file name.
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS" in
    Linux*)   RID_OS="linux"; LIBNAME="libedscrypto.so";   MSYS=0 ;;
    Darwin*)  RID_OS="osx";   LIBNAME="libedscrypto.dylib"; MSYS=0 ;;
    MINGW*|MSYS*|CYGWIN*) RID_OS="win"; LIBNAME="edscrypto.dll"; MSYS=1 ;;
    *) echo "Unsupported OS: $OS" >&2; exit 1 ;;
esac
case "$ARCH" in
    x86_64|amd64) RID_ARCH="x64" ;;
    aarch64|arm64) RID_ARCH="arm64" ;;
    *) RID_ARCH="$ARCH" ;;
esac
RID="${RID_OS}-${RID_ARCH}"

echo ">> Building edscrypto for RID: $RID"
mkdir -p "$BUILD"
cmake -S "$NATIVE" -B "$BUILD" -DCMAKE_BUILD_TYPE=Release ${EDS_TESTS:+-DEDS_BUILD_TESTS=ON}
cmake --build "$BUILD" --config Release -j

# Locate the produced shared library (cmake may place it under a config subdir).
SRC="$(find "$BUILD" -name "$LIBNAME" -type f | head -n1 || true)"
if [ -z "$SRC" ]; then
    echo "ERROR: built library $LIBNAME not found under $BUILD" >&2
    exit 1
fi

DEST="$ROOT/src/Eds.Core/runtimes/$RID/native"
mkdir -p "$DEST"
cp "$SRC" "$DEST/"
echo ">> Staged $LIBNAME -> $DEST/"

if [ "${EDS_TESTS:-}" = "1" ]; then
    KAT="$(find "$BUILD" -name 'kat_test*' -type f -perm -u+x | head -n1 || true)"
    [ -n "$KAT" ] && { echo ">> Running native KAT:"; "$KAT"; }
fi

echo ">> Done."
