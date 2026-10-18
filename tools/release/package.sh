#!/usr/bin/env bash
# Package the Unity builds into release zips + manifest.
#
#   tools/release/package.sh              # both platforms (required for a release)
#   tools/release/package.sh --only mac   # just one (publish.sh will still refuse)
#   tools/release/package.sh --only win
#
# Inputs : Build/mac/Wildmere.app   (BuildScript.BuildMac)
#          Build/win/               (BuildScript.BuildWindows: Wildmere.exe, Wildmere_Data/, UnityPlayer.dll, ...)
# Outputs: dist/Wildmere-<ver>-macOS.zip        (the .app bundle, zipped with ditto)
#          dist/Wildmere-<ver>-Windows-x64.zip  (everything under a top folder Wildmere/)
#          dist/Wildmere-<ver>-SHA256SUMS.txt
#          dist/manifest.json                   (sizes + sha256, read by publish.sh)
#
# The zips are written without macOS extended attributes / AppleDouble "._" files
# (ditto --norsrc --noextattr --noqtn --noacl). With them, extracting on any tool other
# than Archive Utility drops "._*" files into the bundle and breaks its signature.
source "$(dirname "$0")/common.sh"

ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --only) ONLY="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done
case "$ONLY" in ""|mac|win) ;; *) die "--only must be mac or win" ;; esac

VER="$(read_version)"
info "Wildmere $VER: packaging from $BUILD_DIR into $DIST_DIR"
require ditto "packaging must run on macOS"
require codesign
require unzip
mkdir -p "$DIST_DIR"

ZIPFLAGS=(-c -k --keepParent --norsrc --noextattr --noqtn --noacl)
ASSETS_JSON=()
SUMS=()

check_zip_clean() { # no AppleDouble / __MACOSX entries
  if unzip -Z1 "$1" | grep -Eq '(^|/)\._|(^|/)__MACOSX/|(^|/)\.DS_Store$'; then
    die "$1 contains macOS metadata entries (._*, __MACOSX, .DS_Store)"
  fi
}

package_mac() {
  local app="$BUILD_DIR/mac/Wildmere.app"
  local plist="$app/Contents/Info.plist"
  [ -d "$app" ] || die "missing $app (run tools/build.sh / BuildScript.BuildMac first)"
  [ -f "$plist" ] || die "missing $plist"
  local exe bundle_ver
  exe="$(plutil -extract CFBundleExecutable raw -o - "$plist" 2>/dev/null)" || die "Info.plist has no CFBundleExecutable"
  [ -f "$app/Contents/MacOS/$exe" ] && [ -x "$app/Contents/MacOS/$exe" ] || die "missing executable Contents/MacOS/$exe"
  [ "$exe" = "Wildmere" ] || warn "main executable is '$exe', expected 'Wildmere' (PlayerSettings.productName); CI and verify scripts read it from Info.plist anyway"
  [ -d "$app/Contents/Resources/Data" ] || die "missing Contents/Resources/Data (incomplete Unity build)"
  bundle_ver="$(plutil -extract CFBundleShortVersionString raw -o - "$plist" 2>/dev/null || echo '?')"
  if [ "$bundle_ver" != "$VER" ]; then
    if [ "${WM_ALLOW_VERSION_MISMATCH:-0}" = "1" ]; then warn "Info.plist version $bundle_ver != VERSION $VER (allowed by WM_ALLOW_VERSION_MISMATCH=1)"
    else die "Info.plist version is $bundle_ver but VERSION is $VER: stale build? Rebuild, or set WM_ALLOW_VERSION_MISMATCH=1"; fi
  fi
  if ! codesign --verify --deep --strict "$app" 2>/tmp/wm-codesign.$$; then
    cat /tmp/wm-codesign.$$ >&2; rm -f /tmp/wm-codesign.$$
    die "code signature is missing or invalid; Apple Silicon will refuse to run it. Fix: codesign --force --deep --sign - \"$app\""
  fi
  rm -f /tmp/wm-codesign.$$
  local sigkind; sigkind="$(codesign -dv "$app" 2>&1 | sed -n 's/^Signature=//p')"
  local archs; archs="$(lipo -archs "$app/Contents/MacOS/$exe" 2>/dev/null || echo unknown)"
  local label
  local arm=0 intel=0
  [[ " $archs " == *" arm64 "* ]] && arm=1
  [[ " $archs " == *" x86_64 "* ]] && intel=1
  if [ "$arm$intel" = 11 ]; then label="macOS (Apple Silicon & Intel)"
  elif [ "$arm" = 1 ]; then label="macOS (Apple Silicon)"; warn "not a Universal build (archs: $archs); Intel Macs will not run it"
  elif [ "$intel" = 1 ]; then label="macOS (Intel)"; warn "Intel-only build (archs: $archs)"
  else label="macOS"; warn "could not read architectures ($archs)"; fi
  local name; name="$(mac_zip_name "$VER")"
  local out="$DIST_DIR/$name"
  rm -f "$out"
  info "zipping $app ($archs, signature: ${sigkind:-unknown})"
  ditto "${ZIPFLAGS[@]}" "$app" "$out"
  check_zip_clean "$out"
  # round-trip with plain unzip (what non-Apple tools do) and re-verify the signature
  local rt; rt="$(mktemp -d)"
  unzip -q "$out" -d "$rt"
  [ -d "$rt/Wildmere.app" ] || die "zip does not contain Wildmere.app at its top level"
  codesign --verify --deep --strict "$rt/Wildmere.app" || die "signature does not survive a zip round-trip"
  rm -rf "$rt"
  local size sha; size="$(size_of "$out")"; sha="$(sha256_of "$out")"
  ok "$name  $(human_mb "$size")  sha256 $sha"
  local archs_json; archs_json="$(printf '"%s",' $archs)"; archs_json="[${archs_json%,}]"
  ASSETS_JSON+=("{\"platform\":\"macos\",\"label\":\"$label\",\"file\":\"$name\",\"size\":$size,\"sha256\":\"$sha\",\"archs\":$archs_json}")
  SUMS+=("$sha  $name")
}

package_win() {
  local w="$BUILD_DIR/win"
  [ -d "$w" ] || die "missing $w (run BuildScript.BuildWindows first)"
  local f
  for f in Wildmere.exe UnityPlayer.dll; do [ -f "$w/$f" ] || die "Windows build is missing $f"; done
  [ -d "$w/Wildmere_Data" ] || die "Windows build is missing Wildmere_Data/"
  [ -f "$w/Wildmere_Data/globalgamemanagers" ] || die "Wildmere_Data/ looks incomplete (no globalgamemanagers)"
  if [ ! -d "$w/MonoBleedingEdge" ] && [ ! -f "$w/GameAssembly.dll" ]; then
    die "Windows build has neither MonoBleedingEdge/ (Mono) nor GameAssembly.dll (IL2CPP)"
  fi
  [ "$(head -c 2 "$w/Wildmere.exe")" = "MZ" ] || die "Wildmere.exe is not a Windows executable"
  local stage; stage="$(mktemp -d)"
  # ship the whole folder, minus Unity's never-ship debug folders and Finder litter
  rsync -a --exclude '*_DoNotShip' --exclude '*_ButDontShipItWithYourGame' --exclude '.DS_Store' --exclude '._*' "$w/" "$stage/Wildmere/"
  local name; name="$(win_zip_name "$VER")"
  local out="$DIST_DIR/$name"
  rm -f "$out"
  info "zipping $w as Wildmere/ ($(find "$stage/Wildmere" -type f | wc -l | tr -d ' ') files)"
  ditto "${ZIPFLAGS[@]}" "$stage/Wildmere" "$out"
  rm -rf "$stage"
  check_zip_clean "$out"
  unzip -Z1 "$out" | grep -qx 'Wildmere/Wildmere.exe' || die "zip layout wrong: Wildmere/Wildmere.exe not found"
  local size sha; size="$(size_of "$out")"; sha="$(sha256_of "$out")"
  ok "$name  $(human_mb "$size")  sha256 $sha"
  ASSETS_JSON+=("{\"platform\":\"windows\",\"label\":\"Windows 10/11 (64-bit)\",\"file\":\"$name\",\"size\":$size,\"sha256\":\"$sha\"}")
  SUMS+=("$sha  $name")
}

[ "$ONLY" = "win" ] || package_mac
[ "$ONLY" = "mac" ] || package_win

# Remove stale zips from other versions so publish.sh never picks them up by accident.
for f in "$DIST_DIR"/Wildmere-*.zip; do
  [ -e "$f" ] || continue
  case "$(basename "$f")" in "$(mac_zip_name "$VER")"|"$(win_zip_name "$VER")") ;; *) warn "removing stale $(basename "$f")"; rm -f "$f" ;; esac
done

printf '%s\n' "${SUMS[@]}" > "$DIST_DIR/$(sums_name "$VER")"
{
  printf '{\n  "version": "%s",\n  "tag": "v%s",\n  "created": "%s",\n  "complete": %s,\n  "assets": [\n' \
    "$VER" "$VER" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$([ -z "$ONLY" ] && echo true || echo false)"
  for i in "${!ASSETS_JSON[@]}"; do
    printf '    %s%s\n' "${ASSETS_JSON[$i]}" "$([ "$i" -lt $((${#ASSETS_JSON[@]} - 1)) ] && echo , || true)"
  done
  printf '  ]\n}\n'
} > "$DIST_DIR/manifest.json"

ok "wrote $DIST_DIR/manifest.json and $(sums_name "$VER")"
[ -z "$ONLY" ] || warn "partial package (--only $ONLY): publish.sh will refuse until both platforms are packaged"
say ""
say "Next: tools/release/verify-download.sh --local   (pre-flight the zips)"
say "      tools/release/publish.sh                   (needs releases/v$VER.md)"
