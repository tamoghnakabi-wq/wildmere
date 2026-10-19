#!/usr/bin/env bash
# End-to-end test of the in-game updater on macOS (no network: a local feed on 127.0.0.1:8765).
#   tools/release/test-updater-mac.sh            # builds "old" (VERSION) and "new" (0.1.99) apps, then:
#     1. good update: old app checks, downloads, verifies, installs, relaunches as 0.1.99
#     2. tampered manifest: signature fails, nothing installs
#     3. wrong sha256 in a validly signed manifest: download rejected, nothing installs
#     4. same version in the feed: reports up to date
# Uses the real signing key in .secrets/ (the game only trusts the embedded public key).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; cd "$ROOT"
W="${WM_UPD_DIR:-/tmp/wm-updater-e2e}"; FEED="$W/feed"; INST="$W/install"
KEY=.secrets/wildmere-update-key.pem
VER="$(tr -d '[:space:]' < VERSION)"; NEWVER=0.1.99
UPD="$HOME/Library/Application Support/Wildmere/Wildmere/Updates"
pass=0; fail=0
check() { if [ "$1" = 0 ]; then echo "PASS  $2"; pass=$((pass+1)); else echo "FAIL  $2"; fail=$((fail+1)); fi; }

rm -rf "$W"; mkdir -p "$FEED" "$INST"
if [ -z "${SKIP_BUILD:-}" ]; then
  tools/build.sh mac | grep -E "BUILD_RESULT|error CS" || true
  ditto Build/mac/Wildmere.app "$W/old.app"
  echo "$NEWVER" > VERSION
  tools/build.sh mac | grep -E "BUILD_RESULT|error CS" || true
  echo "$VER" > VERSION
  ditto -c -k --norsrc --noextattr --noqtn --noacl --keepParent Build/mac/Wildmere.app "$FEED/Wildmere-$NEWVER-macOS.zip"
  tools/build.sh mac | grep -E "BUILD_RESULT|error CS" || true   # leave Build/mac at the real version
  cp -R "$W/old.app" "$W/old.app.keep" 2>/dev/null
fi
ZIP="$FEED/Wildmere-$NEWVER-macOS.zip"
SIZE=$(stat -f%z "$ZIP"); SHA=$(shasum -a 256 "$ZIP" | awk '{print $1}')

manifest() { # version sha
  printf '{\n  "schema": 1,\n  "version": "%s",\n  "date": "2026-10-19",\n  "summary": "Updater end-to-end test build.",\n  "assets": {\n    "macos": {"file": "Wildmere-%s-macOS.zip", "url": "http://127.0.0.1:8765/Wildmere-%s-macOS.zip", "size": %s, "sha256": "%s"},\n    "windows": {"file": "x.zip", "url": "http://127.0.0.1:8765/x.zip", "size": 1, "sha256": "%s"}\n  }\n}\n' "$1" "$NEWVER" "$NEWVER" "$SIZE" "$2" "$2" > "$FEED/wildmere-update.json"
  openssl dgst -sha256 -sign "$KEY" -out "$FEED/wildmere-update.json.sig" "$FEED/wildmere-update.json"
}
run_old() { # name ; runs a fresh copy of the old app until it exits (or 120 s)
  rm -rf "$INST/Wildmere.app" "$INST/Wildmere.app.previous"; ditto "$W/old.app" "$INST/Wildmere.app"
  rm -rf "$UPD"
  WM_UPDATE_RELAUNCH_ARGS="-batchmode -nographics -logFile $W/$1-new.log -quitafter 12" \
    "$INST/Wildmere.app/Contents/MacOS/Wildmere" -batchmode -nographics -logFile "$W/$1-old.log" \
    -updatefeed http://127.0.0.1:8765/wildmere-update.json -updatetest -quitafter 45 >/dev/null 2>&1
}
installed_version() { plutil -extract CFBundleShortVersionString raw -o - "$INST/Wildmere.app/Contents/Info.plist" 2>/dev/null; }

(cd "$FEED" && exec python3 -m http.server 8765 --bind 127.0.0.1 >/dev/null 2>&1) & SRV=$!
trap 'kill $SRV 2>/dev/null' EXIT
sleep 1

# 1. good update
manifest "$NEWVER" "$SHA"; run_old good
for i in $(seq 1 60); do grep -q "WM_UPDATE installed" "$W/good-new.log" 2>/dev/null && break; sleep 1; done
grep -q "WM_UPDATE ready version=$NEWVER" "$W/good-old.log"; check $? "old app downloaded + verified $NEWVER"
cp "$UPD/install.log" "$W/good-install.log" 2>/dev/null; cat "$W/good-install.log" 2>/dev/null | sed "s/^/      helper: /"
grep -q "WM_UPDATE installed from=$VER to=$NEWVER" "$W/good-new.log" 2>/dev/null; check $? "relaunched app reports installed from=$VER to=$NEWVER"
[ "$(installed_version)" = "$NEWVER" ]; check $? "installed bundle is now $NEWVER ($(installed_version))"
[ ! -e "$INST/Wildmere.app.previous" ]; check $? "backup removed after a successful swap"
codesign --verify --deep "$INST/Wildmere.app" 2>/dev/null; check $? "installed app signature valid"

# 2. tampered manifest
manifest "$NEWVER" "$SHA"; sed -i '' 's/end-to-end test/tampered!/' "$FEED/wildmere-update.json"; run_old tamper
grep -q "WM_UPDATE FAIL manifest signature" "$W/tamper-old.log"; check $? "tampered manifest rejected"
[ "$(installed_version)" = "$VER" ]; check $? "nothing installed after tampering ($(installed_version))"

# 3. valid signature, wrong file hash
manifest "$NEWVER" "0000000000000000000000000000000000000000000000000000000000000000"; run_old badsha
grep -q "WM_UPDATE FAIL downloaded file does not match" "$W/badsha-old.log"; check $? "download with wrong sha256 rejected"
[ "$(installed_version)" = "$VER" ]; check $? "nothing installed after hash mismatch"

# 4. not newer
manifest "$VER" "$SHA"; run_old same
grep -q "WM_UPDATE checked current=$VER latest=$VER" "$W/same-old.log" && ! grep -q "WM_UPDATE installing" "$W/same-old.log"; check $? "same version: up to date, no install"

# 5. install on quit (no relaunch), windowed, with a screenshot of the title-screen update card
manifest "$NEWVER" "$SHA"
rm -rf "$INST/Wildmere.app" "$INST/Wildmere.app.previous"; ditto "$W/old.app" "$INST/Wildmere.app"; rm -rf "$UPD"
WM_UPDATE_TEST_MODE=quit WM_UPDATE_SHOT="$W/title-update-card.png" \
  "$INST/Wildmere.app/Contents/MacOS/Wildmere" -logFile "$W/quit-old.log" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 \
  -updatefeed http://127.0.0.1:8765/wildmere-update.json -updatetest -quitafter 60 >/dev/null 2>&1
for i in $(seq 1 30); do [ "$(installed_version)" = "$NEWVER" ] && break; sleep 1; done
grep -q "WM_UPDATE test quitting normally" "$W/quit-old.log" && grep -q "relaunch=False" "$W/quit-old.log"; check $? "quit path launched the installer without relaunch"
[ "$(installed_version)" = "$NEWVER" ]; check $? "installed on quit ($(installed_version))"
[ -s "$W/title-update-card.png" ]; check $? "title screen captured with the update card"

rm -rf "$UPD"
echo "updater e2e: pass=$pass fail=$fail (logs in $W)"
[ "$fail" = 0 ]
