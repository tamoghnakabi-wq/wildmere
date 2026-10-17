# Shared helpers for tools/release/*.sh (sourced, not executed).
# Overridable with environment variables so the scripts can be tested on fake builds:
#   WM_BUILD_DIR (default <repo>/Build)   WM_DIST_DIR (default <repo>/dist)
#   WM_REPO      (default tamoghnakabi-wq/wildmere)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="${WM_BUILD_DIR:-$REPO_ROOT/Build}"
DIST_DIR="${WM_DIST_DIR:-$REPO_ROOT/dist}"
GH_REPO="${WM_REPO:-tamoghnakabi-wq/wildmere}"
SITE_RELEASE_JSON="$REPO_ROOT/website/src/data/release.json"

if [ -t 1 ]; then
  C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YLW=$'\033[33m'; C_DIM=$'\033[2m'; C_OFF=$'\033[0m'
else
  C_RED=""; C_GRN=""; C_YLW=""; C_DIM=""; C_OFF=""
fi

say()  { printf '%s\n' "$*"; }
info() { printf '%s==>%s %s\n' "$C_DIM" "$C_OFF" "$*"; }
ok()   { printf '%sOK%s   %s\n' "$C_GRN" "$C_OFF" "$*"; }
warn() { printf '%sWARN%s %s\n' "$C_YLW" "$C_OFF" "$*" >&2; }
die()  { printf '%sERROR%s %s\n' "$C_RED" "$C_OFF" "$*" >&2; exit 1; }

read_version() {
  [ -f "$REPO_ROOT/VERSION" ] || die "VERSION file not found at $REPO_ROOT/VERSION"
  local v
  v="$(tr -d ' \t\r\n' < "$REPO_ROOT/VERSION")"
  [[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "VERSION '$v' is not semver (e.g. 0.1.0)"
  printf '%s' "$v"
}

mac_zip_name() { printf 'Wildmere-%s-macOS.zip' "$1"; }
win_zip_name() { printf 'Wildmere-%s-Windows-x64.zip' "$1"; }
sums_name()    { printf 'Wildmere-%s-SHA256SUMS.txt' "$1"; }

sha256_of() {
  if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
  else sha256sum "$1" | awk '{print $1}'; fi
}

size_of() {
  if stat -f %z "$1" >/dev/null 2>&1; then stat -f %z "$1"; else stat -c %s "$1"; fi
}

human_mb() { awk -v b="$1" 'BEGIN { printf "%.1f MB", b / 1000000 }'; }

require() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed${2:+ ($2)}"; }

# run_with_timeout <seconds> <cmd...>  (macOS has no coreutils `timeout`)
# Exit status 142 means the alarm fired.
run_with_timeout() {
  local secs="$1"; shift
  if command -v timeout >/dev/null 2>&1; then
    timeout --signal=KILL "$secs" "$@"
  else
    perl -e 'alarm shift @ARGV; exec @ARGV or die "exec failed: $!\n"' "$secs" "$@"
  fi
}

# The machine-wide Unity slot lock from tools/unity.sh: at most two Unity processes
# (editor or player) at once on the 16 GB MacBook Air.
WM_SLOT=""
acquire_unity_slot() {
  while true; do
    for n in 1 2; do
      local d="/tmp/wildmere-unity-slot-$n"
      if mkdir "$d" 2>/dev/null; then echo $$ > "$d/pid"; WM_SLOT="$d"; return; fi
      local p; p="$(cat "$d/pid" 2>/dev/null || true)"
      if [ -n "$p" ] && ! kill -0 "$p" 2>/dev/null; then rm -rf "$d"; fi
    done
    sleep 3
  done
}
release_unity_slot() { [ -n "$WM_SLOT" ] && rm -rf "$WM_SLOT"; WM_SLOT=""; }
