#!/bin/bash
# Builds COMET.app, a self-contained macOS application bundle.
#
# Usage:  scripts/build-app.sh [Release|Debug]
#
# Output: build/COMET.app
#
# The bundle is what makes this a real Mac app rather than a console process
# that happens to draw a window: launchd gives it a window server connection,
# a Dock icon, and the system menu bar. Running the binary directly from a
# terminal without the bundle fails to start the render timer.

set -euo pipefail

CONFIG="${1:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Comet.Desktop/Comet.Desktop.csproj"
BUILD="$ROOT/build"
APP="$BUILD/COMET.app"

# Apple Silicon by default; pass osx-x64 in RID for an Intel build.
RID="${RID:-osx-arm64}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$HOME/.dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
fi

echo "==> Publishing $CONFIG for $RID"
PUBLISH_DIR="$BUILD/publish-$RID"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PUBLISH_DIR"

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
cp "$ROOT/build/Info.plist" "$APP/Contents/Info.plist"

if [ -f "$ROOT/build/COMET.icns" ]; then
    cp "$ROOT/build/COMET.icns" "$APP/Contents/Resources/COMET.icns"
fi

chmod +x "$APP/Contents/MacOS/Comet.Desktop"

# libusb lets COMET open vendor-class USB interfaces, such as a cellular modem's
# AT port, which macOS never exposes under /dev. It is optional: without it the
# app lists serial ports only. LGPL-2.1, loaded dynamically, licence alongside.
for LIBUSB in /opt/homebrew/lib/libusb-1.0.0.dylib /usr/local/lib/libusb-1.0.0.dylib; do
    if [ -f "$LIBUSB" ]; then
        echo "==> Bundling $LIBUSB"
        cp -L "$LIBUSB" "$APP/Contents/MacOS/libusb-1.0.0.dylib"
        LIBUSB_ROOT="$(cd "$(dirname "$(readlink -f "$LIBUSB")")/.." && pwd)"
        if [ -f "$LIBUSB_ROOT/COPYING" ]; then
            cp "$LIBUSB_ROOT/COPYING" "$APP/Contents/Resources/libusb-COPYING.txt"
        fi
        break
    fi
done

# Command files from the repo go beside the app, the folder COMET reads for the
# A B C D header buttons and Load Commands. --update keeps any copy there that is
# newer, so commands saved from inside COMET survive a rebuild.
echo "==> Copying command files beside the app"
for item in "$ROOT"/Button*.txt "$ROOT/COMMANDS"; do
    [ -e "$item" ] && rsync -a --update "$item" "$BUILD/"
done

# An ad-hoc signature is enough for local use and keeps Gatekeeper from
# refusing to launch an unsigned arm64 binary outright. Distributing to
# other machines needs a Developer ID signature and notarisation instead.
echo "==> Signing (ad-hoc)"
codesign --force --deep --sign - "$APP" 2>&1 | sed 's/^/    /' || true

echo
echo "Built $APP"
echo "Run it with:  open '$APP'"
