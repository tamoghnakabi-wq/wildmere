#!/bin/bash
# Launch the built macOS player with harness flags and echo the important log lines.
#   usage: tools/run.sh [player args...]      e.g. tools/run.sh -selftest
#   WM_RUNLOG=<path> to choose the log file; WM_RES=1600x900 for the window size.
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
APP="$PROJ/Build/mac/Wildmere.app/Contents/MacOS/Wildmere"
LOG="${WM_RUNLOG:-/tmp/wm-run.log}"
RES="${WM_RES:-1600x900}"
[ -x "$APP" ] || { echo "no build at $APP"; exit 1; }
rm -f "$LOG"
"$APP" -logFile "$LOG" -screen-fullscreen 0 -screen-width "${RES%x*}" -screen-height "${RES#*x}" "$@" >/dev/null 2>&1
rc=$?
echo "--- player exit=$rc ---"
grep -aE "^WM_|Exception|NullReference|error" "$LOG" | grep -v "^WM_GFX" | head -120
exit $rc
