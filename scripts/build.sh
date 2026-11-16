#!/bin/bash
# Builds build/Peekaboo.app (release, ad-hoc signed). Needs only the Command Line Tools.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
if [ ! -f build/AppIcon.icns ]; then
  swift scripts/make-icon.swift build/icon-1024.png
  rm -rf build/AppIcon.iconset && mkdir build/AppIcon.iconset
  for s in 16 32 128 256 512; do
    sips -z $s $s build/icon-1024.png --out build/AppIcon.iconset/icon_${s}x${s}.png >/dev/null
    sips -z $((s*2)) $((s*2)) build/icon-1024.png --out build/AppIcon.iconset/icon_${s}x${s}@2x.png >/dev/null
  done
  iconutil -c icns build/AppIcon.iconset -o build/AppIcon.icns
fi
swift build -c release
APP=build/Peekaboo.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Peekaboo "$APP/Contents/MacOS/Peekaboo"
cp scripts/Info.plist "$APP/Contents/Info.plist"
cp build/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
# Ad-hoc signature, but with a designated requirement on the bundle id only, so macOS
# privacy permissions (Input Monitoring, Screen Recording) survive rebuilds.
codesign --force --sign - --timestamp=none -r='designated => identifier "local.peekaboo.app"' "$APP"
echo "Built $APP"
