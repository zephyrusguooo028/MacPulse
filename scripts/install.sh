#!/bin/bash
# Builds and installs MacPulse into /Applications, then launches it.
set -euo pipefail
cd "$(dirname "$0")/.."
./scripts/build.sh
pkill -x MacPulse 2>/dev/null || true
# Wait for the old instance to exit, otherwise `open` can fail with -600.
for _ in $(seq 1 20); do pgrep -x MacPulse >/dev/null || break; sleep 0.25; done
rm -rf /Applications/MacPulse.app
cp -R build/MacPulse.app /Applications/
open /Applications/MacPulse.app
echo "Installed /Applications/MacPulse.app"
