#!/usr/bin/env bash
# Test the real download + install flow for a release.
#
#   tools/release/verify-download.sh v0.1.0     # download the published assets and test them
#   tools/release/verify-download.sh --local    # pre-flight dist/ (from package.sh) before publishing
#
# Options:
#   --gh          download with `gh release download` instead of the website's URLs
#   --rosetta     also run the Intel (x86_64) slice of the macOS app under Rosetta 2
#   --no-run      skip launching the player (checksums + structure + signature only)
#   --keep        keep the temp folder for inspection
#   --timeout N   seconds before the self-test is killed (default 600)
#
# Steps: fresh temp dir -> download (curl from the URLs in website/src/data/release.json
# when it matches the tag, else gh) -> sha256 vs release.json (or SHA256SUMS) -> extract
# (ditto, like Archive Utility; Windows zip with unzip) -> macOS: codesign + architectures +
# Gatekeeper report, then `Wildmere -batchmode -nographics -selftest -logFile -`.
# PASS needs exit code 0 and a "WM_SELFTEST PASS" line. Windows can only be checked for
# layout here; verify-release.yml runs it on a real Windows runner.
source "$(dirname "$0")/common.sh"
LIB="$(dirname "$0")/lib/release-json.mjs"

TAG=""; LOCAL=0; USE_GH=0; ROSETTA=0; NORUN=0; KEEP=0; TIMEOUT=600
while [ $# -gt 0 ]; do
  case "$1" in
    --local) LOCAL=1; shift ;;
    --gh) USE_GH=1; shift ;;
    --rosetta) ROSETTA=1; shift ;;
    --no-run) NORUN=1; shift ;;
    --keep) KEEP=1; shift ;;
    --timeout) TIMEOUT="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    v*) TAG="$1"; shift ;;
    *) die "unknown argument: $1 (expected a tag like v0.1.0, or --local)" ;;
  esac
done
[ "$LOCAL" = 1 ] || [ -n "$TAG" ] || die "usage: verify-download.sh <tag> | --local   (see --help)"
require node; require unzip

WORK="$(mktemp -d "${TMPDIR:-/tmp}/wildmere-verify.XXXXXX")"
cleanup() { release_unity_slot; if [ "$KEEP" = 1 ]; then say "kept $WORK"; else rm -rf "$WORK"; fi; }
trap cleanup EXIT
mkdir -p "$WORK/dl"
RESULTS=()
FAILED=0
record() { RESULTS+=("$(printf '%-8s %-34s %s' "$1" "$2" "$3")"); [ "$1" != FAIL ] || FAILED=1; }

# ---- 1. get the files + the checksums we expect ---------------------------------------
EXPECT="$WORK/expect.tsv" # file<TAB>sha256
if [ "$LOCAL" = 1 ]; then
  VER="$(read_version)"; TAG="v$VER"
  info "pre-flight of dist/ for $TAG"
  node "$LIB" manifest "$DIST_DIR/manifest.json" "$VER" | while IFS=$'\t' read -r _ file _ sha; do
    printf '%s\t%s\n' "$file" "$sha" >> "$EXPECT"; cp -c "$DIST_DIR/$file" "$WORK/dl/$file" 2>/dev/null || cp "$DIST_DIR/$file" "$WORK/dl/$file"
  done
  SOURCE="dist/manifest.json"
elif [ "$USE_GH" = 0 ] && node "$LIB" expect "$SITE_RELEASE_JSON" "$TAG" > "$WORK/site.tsv" 2>/dev/null; then
  info "downloading $TAG with the URLs the website serves (website/src/data/release.json)"
  while IFS=$'\t' read -r file url sha; do
    say "  $url"
    curl -fL --retry 3 --retry-delay 2 --silent --show-error -o "$WORK/dl/$file" "$url" || die "download failed: $url"
    printf '%s\t%s\n' "$file" "$sha" >> "$EXPECT"
  done < "$WORK/site.tsv"
  SOURCE="website release.json"
else
  require gh
  [ "$USE_GH" = 1 ] || warn "website release.json is not on $TAG yet; using gh and the release's SHA256SUMS"
  info "downloading $TAG from $GH_REPO with gh"
  gh release download "$TAG" --repo "$GH_REPO" --pattern 'Wildmere-*' --dir "$WORK/dl" || die "gh release download failed"
  sums="$(ls "$WORK"/dl/*-SHA256SUMS.txt 2>/dev/null | head -1)"
  [ -n "$sums" ] || die "release has no SHA256SUMS asset to check against"
  awk '{print $2 "\t" $1}' "$sums" > "$EXPECT"
  gh release view "$TAG" --repo "$GH_REPO" --json assets > "$WORK/gh.json"
  SOURCE="$(basename "$sums") + GitHub digests"
fi
[ -s "$EXPECT" ] || die "nothing to verify"

# ---- 2. checksums ------------------------------------------------------------------------
while IFS=$'\t' read -r file sha; do
  f="$WORK/dl/$file"
  if [ ! -f "$f" ]; then record FAIL "$file" "not downloaded"; continue; fi
  got="$(sha256_of "$f")"
  if [ "$got" = "$sha" ]; then
    if [ -f "$WORK/gh.json" ]; then
      digest="$(node -p "(require('$WORK/gh.json').assets.find(a => a.name === '$file') || {}).digest || ''")"
      if [ -n "$digest" ] && [ "$digest" != "sha256:$got" ]; then record FAIL "$file" "GitHub digest $digest differs"; continue; fi
    fi
    record PASS "$file" "sha256 matches $SOURCE ($(human_mb "$(size_of "$f")"))"
  else
    record FAIL "$file" "sha256 $got != expected $sha"
  fi
done < "$EXPECT"

MAC_ZIP="$(ls "$WORK"/dl/Wildmere-*-macOS.zip 2>/dev/null | head -1 || true)"
WIN_ZIP="$(ls "$WORK"/dl/Wildmere-*-Windows-x64.zip 2>/dev/null | head -1 || true)"

# ---- 3. Windows: layout only (cannot run it here) -----------------------------------------
if [ -n "$WIN_ZIP" ]; then
  mkdir -p "$WORK/win"; unzip -q "$WIN_ZIP" -d "$WORK/win"
  w="$WORK/win/Wildmere"; problems=""
  [ -f "$w/Wildmere.exe" ] && [ "$(head -c 2 "$w/Wildmere.exe")" = "MZ" ] || problems+=" Wildmere.exe"
  [ -f "$w/UnityPlayer.dll" ] || problems+=" UnityPlayer.dll"
  { [ -f "$w/Wildmere_Data/globalgamemanagers" ] || [ -f "$w/Wildmere_Data/data.unity3d" ]; } || problems+=" Wildmere_Data/"
  [ -d "$w/MonoBleedingEdge" ] || [ -f "$w/GameAssembly.dll" ] || problems+=" MonoBleedingEdge/|GameAssembly.dll"
  if [ -z "$problems" ]; then record PASS "Windows layout" "Wildmere/ has exe, UnityPlayer.dll, Wildmere_Data, runtime"
  else record FAIL "Windows layout" "missing:$problems"; fi
  record SKIP "Windows self-test" "needs Windows: see verify-release.yml on GitHub Actions"
fi

# ---- 4. macOS: extract like Archive Utility, check signature, run the self-test ------------
if [ -n "$MAC_ZIP" ]; then
  if [ "$(uname -s)" != Darwin ]; then
    record SKIP "macOS checks" "not running on macOS"
  else
    mkdir -p "$WORK/mac"; ditto -x -k "$MAC_ZIP" "$WORK/mac"
    app="$WORK/mac/Wildmere.app"
    if [ ! -d "$app" ]; then
      record FAIL "macOS layout" "zip has no Wildmere.app at the top level"
    else
      exe="$(plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist" 2>/dev/null || echo Wildmere)"
      bin="$app/Contents/MacOS/$exe"
      if codesign --verify --deep --strict "$app" 2>"$WORK/codesign.txt"; then
        kind="$(codesign -dv "$app" 2>&1 | sed -n 's/^Signature=//p')"
        record PASS "macOS signature" "valid (${kind:-signed}); Apple Silicon will load it"
      else
        record FAIL "macOS signature" "codesign --verify failed: $(head -1 "$WORK/codesign.txt")"
      fi
      archs="$(lipo -archs "$bin" 2>/dev/null || echo '?')"
      if [[ " $archs " == *" arm64 "* && " $archs " == *" x86_64 "* ]]; then record PASS "macOS architectures" "$archs (Universal)"
      else record WARN "macOS architectures" "$archs (not Universal)"; fi
      if spctl --assess --type execute "$app" >/dev/null 2>&1; then gk="accepted"; else gk="rejected (expected: not notarized; users click Open Anyway)"; fi
      record INFO "Gatekeeper" "$gk"

      run_selftest() { # <label> <arch-prefix...>
        local label="$1"; shift
        local log="$WORK/selftest-$label.log"
        info "self-test ($label): $* \"$bin\" -batchmode -nographics -selftest -logFile -"
        acquire_unity_slot
        set +e
        run_with_timeout "$TIMEOUT" "$@" "$bin" -batchmode -nographics -selftest -logFile - > "$log" 2>&1
        local rc=$?
        set -e
        release_unity_slot
        # "|| true": an empty log (e.g. killed by the timeout) must not abort under pipefail
        { grep -a '^WM_' "$log" || true; } | tail -20 | sed 's/^/    /'
        local tail_line; tail_line="$({ grep -a 'WM_SELFTEST' "$log" || true; } | tail -1)"
        if [ "$rc" = 0 ] && grep -aq 'WM_SELFTEST PASS' "$log"; then
          record PASS "self-test ($label)" "exit 0, ${tail_line:-WM_SELFTEST PASS}"
        elif [ "$rc" = 142 ] || [ "$rc" = 137 ]; then
          record FAIL "self-test ($label)" "timed out after ${TIMEOUT}s (log: $log)"
        else
          record FAIL "self-test ($label)" "exit $rc, ${tail_line:-no WM_SELFTEST PASS line} (log: $log)"
        fi
        [ "$KEEP" = 1 ] || cp "$log" "${TMPDIR:-/tmp}/wildmere-selftest-$label.log" 2>/dev/null || true
      }

      if [ "$NORUN" = 1 ]; then
        record SKIP "self-test" "--no-run"
      else
        host="$(uname -m)"
        if [[ " $archs " == *" $host "* ]]; then run_selftest "$host"
        elif [ "$host" = arm64 ] && [[ " $archs " == *" x86_64 "* ]]; then run_selftest "x86_64-rosetta" arch -x86_64
        else record FAIL "self-test" "app has no slice for this Mac ($host)"; fi
        if [ "$ROSETTA" = 1 ] && [ "$host" = arm64 ] && [[ " $archs " == *" x86_64 "* ]]; then
          if arch -x86_64 /usr/bin/true 2>/dev/null; then run_selftest "x86_64-rosetta" arch -x86_64
          else record SKIP "self-test (x86_64)" "Rosetta 2 not installed (softwareupdate --install-rosetta)"; fi
        fi
      fi
    fi
  fi
fi

# ---- 5. report -------------------------------------------------------------------------
say ""
say "Wildmere $TAG download verification"
printf '%s\n' "${RESULTS[@]}"
say ""
if [ "$FAILED" = 0 ]; then ok "PASS"; else printf '%sFAIL%s\n' "$C_RED" "$C_OFF"; exit 1; fi
