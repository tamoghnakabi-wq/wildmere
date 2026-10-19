#!/usr/bin/env bash
# Stage the Windows updater end-to-end test (see .github/workflows/updater-windows.yml):
# builds a 0.1.99 Windows player, signs a test feed that points at http://127.0.0.1:8765/, and
# uploads both to an existing prerelease that already has the "old" Windows zip.
#   tools/release/stage-updater-test.sh v0.1.3-rc.1
# Note: the signed test feed only ever points at 127.0.0.1 over plain HTTP, which the game refuses
# unless it was started with -updatefeed, so it cannot be abused as a real update.
set -euo pipefail
TAG="${1:?usage: stage-updater-test.sh <prerelease tag>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; cd "$ROOT"
GH_REPO="${GH_REPO:-tamoghnakabi-wq/wildmere}"
VER="$(tr -d '[:space:]' < VERSION)"; NEWVER=0.1.99
OUT="$(mktemp -d)"
trap 'echo "$VER" > VERSION' EXIT

echo "$NEWVER" > VERSION
tools/build.sh win | grep -E "BUILD_RESULT|error CS"
echo "$VER" > VERSION
mkdir -p "$OUT/stage"
rsync -a --exclude '*_DoNotShip' --exclude '*_ButDontShipItWithYourGame' Build/win/ "$OUT/stage/Wildmere/"
ZIP="$OUT/Wildmere-$NEWVER-Windows-x64.zip"
(cd "$OUT/stage" && ditto -c -k --norsrc --noextattr --noqtn --noacl --keepParent Wildmere "$ZIP")
tools/build.sh win | grep -E "BUILD_RESULT|error CS"   # restore Build/win to the real version

SIZE=$(stat -f%z "$ZIP"); SHA=$(shasum -a 256 "$ZIP" | awk '{print $1}')
printf '{\n  "schema": 1,\n  "version": "%s",\n  "date": "test",\n  "summary": "Updater end-to-end test build.",\n  "assets": {\n    "windows": {"file": "Wildmere-%s-Windows-x64.zip", "url": "http://127.0.0.1:8765/Wildmere-%s-Windows-x64.zip", "size": %s, "sha256": "%s"},\n    "macos": {"file": "x.zip", "url": "http://127.0.0.1:8765/x.zip", "size": 1, "sha256": "%s"}\n  }\n}\n' \
  "$NEWVER" "$NEWVER" "$NEWVER" "$SIZE" "$SHA" "$SHA" > "$OUT/updater-test-feed.json"
openssl dgst -sha256 -sign .secrets/wildmere-update-key.pem -out "$OUT/updater-test-feed.json.sig" "$OUT/updater-test-feed.json"
openssl dgst -sha256 -verify tools/release/update-public.pem -signature "$OUT/updater-test-feed.json.sig" "$OUT/updater-test-feed.json"
gh release upload "$TAG" "$ZIP" "$OUT/updater-test-feed.json" "$OUT/updater-test-feed.json.sig" --repo "$GH_REPO" --clobber
echo "staged on $TAG; run: gh workflow run updater-windows.yml --repo $GH_REPO -f tag=$TAG"
