#!/bin/bash
# Builds build/Peekaboo.dmg: open it and drag Peekaboo onto Applications.
set -euo pipefail
cd "$(dirname "$0")/.."
./scripts/build.sh
STAGE=build/dmg
rm -rf "$STAGE" build/Peekaboo.dmg
mkdir -p "$STAGE"
cp -R build/Peekaboo.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Peekaboo" -srcfolder "$STAGE" -ov -format UDZO build/Peekaboo.dmg >/dev/null
rm -rf "$STAGE"
echo "Built build/Peekaboo.dmg"
