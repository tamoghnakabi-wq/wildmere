#!/bin/bash
# Batchmode Unity wrapper that surfaces only what matters.
#   usage: tools/unity.sh <ExecuteMethod|-> [extra unity args...]
# Pass "-" as the method to run Unity with only the extra args (e.g. -runTests).
#
# A machine-wide 2-slot semaphore (/tmp/wildmere-unity-slot-N) keeps parallel
# agents/worktrees from starting more than two Unity processes at once on the
# 16 GB MacBook Air. Stale slots (dead pid) are reclaimed automatically.
UNITY="/Applications/Unity/Hub/Editor/6000.3.22f1/Unity.app/Contents/MacOS/Unity"
PROJ="$(cd "$(dirname "$0")/.." && pwd)"
METHOD="$1"; shift
TAG="$(basename "$PROJ")-$(echo "$METHOD" | tr '.' '-' | tr -d '/')"
LOG="${WM_LOG:-/tmp/wm-$TAG.log}"

SLOT=""
acquire() {
  while true; do
    for n in 1 2; do
      d="/tmp/wildmere-unity-slot-$n"
      if mkdir "$d" 2>/dev/null; then echo $$ > "$d/pid"; SLOT="$d"; return; fi
      p=$(cat "$d/pid" 2>/dev/null)
      if [ -n "$p" ] && ! kill -0 "$p" 2>/dev/null; then rm -rf "$d"; fi
    done
    sleep 3
  done
}
acquire
trap 'rm -rf "$SLOT"' EXIT

if [ "$METHOD" = "-" ]; then
  "$UNITY" -batchmode -projectPath "$PROJ" -logFile "$LOG" "$@" >/dev/null 2>&1
else
  "$UNITY" -batchmode -nographics -projectPath "$PROJ" -executeMethod "$METHOD" -quit \
           -logFile "$LOG" "$@" >/dev/null 2>&1
fi
rc=$?
echo "--- diagnostics ---"
grep -oE "Assets/[^ ]+\([0-9]+,[0-9]+\): error CS[0-9]+: .*" "$LOG" | sort -u | head -40
grep -oE "Shader error in '[^']*': [^\"]*" "$LOG" | sort -u | head -30
grep -E "^BUILD_ERR|^WM_FAIL|Unhandled exception|Scripts have compiler errors" "$LOG" | sort -u | head -20
echo "--- output ---"
grep -aE "^(WM_|BUILD_|TEST_)" "$LOG" | head -200
echo "exit=$rc  log=$LOG"
exit $rc
