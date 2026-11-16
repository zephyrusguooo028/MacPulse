#!/bin/bash
# Builds and installs Peekaboo into /Applications, then launches it.
set -euo pipefail
cd "$(dirname "$0")/.."
./scripts/build.sh
pkill -x Peekaboo 2>/dev/null || true
# Wait for the old instance to exit, otherwise `open` can fail with -600.
for _ in $(seq 1 20); do pgrep -x Peekaboo >/dev/null || break; sleep 0.25; done
rm -rf /Applications/Peekaboo.app
cp -R build/Peekaboo.app /Applications/
# LaunchServices can briefly refuse (-600) right after the old instance exits; retry.
for _ in 1 2 3 4 5; do open /Applications/Peekaboo.app 2>/dev/null && break; sleep 1; done
echo "Installed /Applications/Peekaboo.app"
