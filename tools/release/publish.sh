#!/usr/bin/env bash
# Publish dist/ as GitHub release v<VERSION> and point the website at it.
#
#   tools/release/publish.sh             # do it
#   tools/release/publish.sh --dry-run   # run every check, print the gh commands instead
#
# Needs: dist/ from package.sh (both platforms), releases/v<ver>.md (release notes),
# gh signed in with write access to the repo, and HEAD pushed to GitHub.
#
# Idempotent: re-running after a partial failure uploads only what is missing.
# Assets on an already *published* release are never replaced; a mismatch aborts
# (bump VERSION instead). The release is created as a draft and only published once
# every asset is uploaded and its GitHub sha256 digest matches the local file, so
# `release: published` (which triggers verify-release.yml) fires on a complete release.
source "$(dirname "$0")/common.sh"
LIB="$(dirname "$0")/lib/release-json.mjs"

DRY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run|-n) DRY=1; shift ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done
run() { if [ "$DRY" = 1 ]; then printf '%s[dry-run]%s' "$C_YLW" "$C_OFF"; printf ' %q' "$@"; printf '\n'; else "$@"; fi; }

require gh "https://cli.github.com"
require node
VER="$(read_version)"
TAG="v$VER"
REPO_URL="https://github.com/$GH_REPO"
NOTES="$REPO_ROOT/releases/$TAG.md"
MANIFEST="$DIST_DIR/manifest.json"
SUMS="$DIST_DIR/$(sums_name "$VER")"
info "publishing Wildmere $VER as $TAG on $GH_REPO$([ "$DRY" = 1 ] && echo ' (dry run)')"

# ---- local checks ------------------------------------------------------------------
[ -s "$NOTES" ] || die "release notes missing: $NOTES (copy releases/TEMPLATE.md, see releases/README.md)"
if grep -q '^TODO' "$NOTES"; then die "$NOTES still has TODO lines from the template"; fi

FILES=()
while IFS=$'\t' read -r platform file size sha; do
  path="$DIST_DIR/$file"
  [ -f "$path" ] || die "missing $path"
  [ "$(size_of "$path")" = "$size" ] || die "$file size differs from manifest (re-run package.sh)"
  [ "$(sha256_of "$path")" = "$sha" ] || die "$file sha256 differs from manifest (re-run package.sh)"
  grep -q "^$sha  $file\$" "$SUMS" 2>/dev/null || die "$file not listed correctly in $(basename "$SUMS")"
  ok "$platform  $file  $(human_mb "$size")"
  FILES+=("$path")
done < <(node "$LIB" manifest "$MANIFEST" "$VER") || die "manifest check failed"
[ "${#FILES[@]}" -eq 2 ] || die "expected 2 assets in the manifest, found ${#FILES[@]}"
FILES+=("$SUMS")

# Signed in-game update manifest (the game reads releases/latest/download/wildmere-update.json).
UPDATE_JSON="$DIST_DIR/wildmere-update.json"
"$REPO_ROOT/tools/release/sign-update.sh" >/dev/null || die "could not build/sign the update manifest (tools/release/sign-update.sh)"
"$REPO_ROOT/tools/release/sign-update.sh" --verify >/dev/null || die "update manifest signature does not verify"
ok "update manifest signed  $(basename "$UPDATE_JSON")"
FILES+=("$UPDATE_JSON" "$UPDATE_JSON.sig")

# ---- GitHub checks ---------------------------------------------------------------
gh auth status >/dev/null 2>&1 || die "gh is not signed in (gh auth login)"
if ! gh repo view "$GH_REPO" --json name >/dev/null 2>&1; then
  [ "$DRY" = 1 ] || die "repository $GH_REPO not found or not accessible"
  warn "repository $GH_REPO not found; dry run continues as if the release did not exist yet"
  REPO_MISSING=1
fi
SHA="$(git -C "$REPO_ROOT" rev-parse HEAD)"
[ -z "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=no)" ] || warn "working tree has uncommitted changes; the tag will point at $SHA as committed"
if [ -z "${REPO_MISSING:-}" ]; then
  gh api "repos/$GH_REPO/commits/$SHA" --silent 2>/dev/null || {
    [ "$DRY" = 1 ] && warn "HEAD $SHA is not on GitHub yet (push before publishing)" || die "HEAD $SHA is not on GitHub; push first (git push origin HEAD)"
  }
  if tag_sha="$(gh api "repos/$GH_REPO/commits/$TAG" --jq .sha 2>/dev/null)"; then
    [ "$tag_sha" = "$SHA" ] || warn "tag $TAG already exists on GitHub at ${tag_sha:0:12}, not HEAD ${SHA:0:12}; the release will use the existing tag"
  fi
fi

STATE_DIR="$(mktemp -d)"; STATE_FILE="$STATE_DIR/release.json"; trap 'rm -rf "$STATE_DIR"' EXIT
fetch_state() { gh release view "$TAG" --repo "$GH_REPO" --json tagName,isDraft,publishedAt,url,assets > "$STATE_FILE" 2>/dev/null; }

if [ -n "${REPO_MISSING:-}" ] || ! fetch_state; then
  info "no release $TAG yet: creating it as a draft with all assets"
  run gh release create "$TAG" "${FILES[@]}" --repo "$GH_REPO" --title "Wildmere $VER" \
    --notes-file "$NOTES" --target "$SHA" --draft
  if [ "$DRY" = 1 ]; then
    run gh release edit "$TAG" --repo "$GH_REPO" --draft=false --latest
    say "[dry-run] then rewrite website/src/data/release.json from the published asset URLs"
    ok "dry run complete: all local checks passed"
    exit 0
  fi
  fetch_state || die "release $TAG was not created"
fi

IS_DRAFT="$(node -p "require('$STATE_FILE').isDraft")"
info "release $TAG exists ($([ "$IS_DRAFT" = true ] && echo draft || echo published)); checking assets"
while IFS=$'\t' read -r status file; do
  case "$status" in
    OK) ok "$file already uploaded (digest matches)" ;;
    NODIGEST) warn "$file uploaded, size matches, but GitHub reports no digest to compare" ;;
    MISSING) info "uploading $file"; run gh release upload "$TAG" "$DIST_DIR/$file" --repo "$GH_REPO" --clobber ;;
    MISMATCH)
      if [ "$IS_DRAFT" = true ]; then
        warn "$file on the draft differs from dist/; replacing it"
        run gh release upload "$TAG" "$DIST_DIR/$file" --repo "$GH_REPO" --clobber
      else
        die "$file on the PUBLISHED release differs from dist/. Published binaries are never replaced: bump VERSION and release again."
      fi ;;
    *) die "unexpected compare status '$status' for $file" ;;
  esac
done < <(node "$LIB" compare "$STATE_FILE" "$MANIFEST")
for extra in "$SUMS" "$UPDATE_JSON" "$UPDATE_JSON.sig"; do
  if ! node -e "process.exit(require('$STATE_FILE').assets.some(a => a.name === '$(basename "$extra")') ? 0 : 1)"; then
    info "uploading $(basename "$extra")"
    run gh release upload "$TAG" "$extra" --repo "$GH_REPO" --clobber
  fi
done

# ---- verify, then publish ------------------------------------------------------------
fetch_state || die "could not re-read release $TAG"
BAD="$(node "$LIB" compare "$STATE_FILE" "$MANIFEST" | grep -Ev '^(OK|NODIGEST)' || true)"
[ -z "$BAD" ] || die "release assets still not right after upload:"$'\n'"$BAD"
if [ "$IS_DRAFT" = true ]; then
  run gh release edit "$TAG" --repo "$GH_REPO" --notes-file "$NOTES" --title "Wildmere $VER"
  info "all assets verified: publishing the draft"
  run gh release edit "$TAG" --repo "$GH_REPO" --draft=false --latest
  fetch_state || die "could not re-read release $TAG"
fi
ok "$(node -p "require('$STATE_FILE').url")"

# ---- point the website at the real assets --------------------------------------------
node "$LIB" write "$STATE_FILE" "$MANIFEST" "$NOTES" "$REPO_URL" "$SITE_RELEASE_JSON"
node "$REPO_ROOT/website/scripts/check-data.mjs" || die "website data check failed after writing release.json"

say ""
say "Release $TAG is live. Commit the website data so Vercel redeploys with the downloads:"
say ""
say "  git add website/src/data/release.json"
say "  git commit -m \"Website: downloads for $TAG\""
say "  git push origin HEAD"
say ""
say "verify-release.yml is now testing the downloads on GitHub's Windows and macOS runners:"
say "  gh run list --repo $GH_REPO --workflow verify-release.yml --limit 3"
say "Locally: tools/release/verify-download.sh $TAG"
say "When a platform is confirmed on real hardware, set \"verified\" in release.json (see docs/RELEASING.md)."
