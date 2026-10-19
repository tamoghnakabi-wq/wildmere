#!/usr/bin/env bash
# Build and sign the in-game update manifest for the packaged release.
#
#   tools/release/sign-update.sh            # writes dist/wildmere-update.json + .sig
#   tools/release/sign-update.sh --verify   # only verify an existing pair with the public key
#
# The game downloads https://github.com/<repo>/releases/latest/download/wildmere-update.json and its
# .sig, verifies the RSA-SHA256 signature against the public key compiled into the game
# (Assets/Scripts/Core/Updater.cs; same key as tools/release/update-public.pem), then checks the
# downloaded zip's size and SHA-256 against this manifest before installing anything.
#
# The private key lives only in .secrets/wildmere-update-key.pem (gitignored). If it is lost, players
# can still update by downloading from the website once; then generate a new key, update the public
# key in Updater.cs + update-public.pem, and release.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIST_DIR="$REPO_ROOT/dist"
KEY="$REPO_ROOT/.secrets/wildmere-update-key.pem"
PUB="$REPO_ROOT/tools/release/update-public.pem"
OUT="$DIST_DIR/wildmere-update.json"
GH_REPO="${GH_REPO:-tamoghnakabi-wq/wildmere}"
VER="$(tr -d '[:space:]' < "$REPO_ROOT/VERSION")"
TAG="v$VER"

verify() {
  openssl dgst -sha256 -verify "$PUB" -signature "$OUT.sig" "$OUT" >/dev/null 2>&1 \
    && echo "OK   wildmere-update.json signature verifies with update-public.pem" \
    || { echo "ERROR wildmere-update.json signature does NOT verify"; exit 1; }
}

if [ "${1:-}" = "--verify" ]; then verify; exit 0; fi
[ -f "$KEY" ] || { echo "ERROR missing signing key $KEY (see the header of this script)"; exit 1; }
[ -f "$DIST_DIR/manifest.json" ] || { echo "ERROR run tools/release/package.sh first"; exit 1; }

NOTES="$REPO_ROOT/releases/$TAG.md"
node - "$DIST_DIR/manifest.json" "$NOTES" "$VER" "$GH_REPO" "$OUT" <<'EOF'
const fs = require('fs');
const [manifestPath, notesPath, ver, repo, out] = process.argv.slice(2);
const m = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
// First paragraph of the release notes, without markdown/HTML comments, for the in-game card.
let summary = '';
try {
  const text = fs.readFileSync(notesPath, 'utf8').replace(/<!--[\s\S]*?-->/g, '').trim();
  summary = (text.split(/\n\s*\n/)[0] || '').replace(/[#*_`>]/g, '').trim();
} catch { /* notes optional here; publish.sh requires them */ }
const assets = {};
for (const a of m.assets || []) {
  assets[a.platform] = {
    file: a.file,
    url: `https://github.com/${repo}/releases/download/v${ver}/${a.file}`,
    size: a.size,
    sha256: a.sha256,
  };
}
if (!assets.macos || !assets.windows) { console.error('ERROR manifest needs macos and windows assets'); process.exit(1); }
const doc = { schema: 1, version: ver, date: new Date().toISOString().slice(0, 10), summary, assets };
fs.writeFileSync(out, JSON.stringify(doc, null, 2) + '\n');
console.log(`OK   wrote ${out} (v${ver}, ${Object.keys(assets).join(', ')})`);
EOF
openssl dgst -sha256 -sign "$KEY" -out "$OUT.sig" "$OUT"
echo "OK   signed -> $(basename "$OUT").sig ($(wc -c < "$OUT.sig" | tr -d ' ') bytes)"
verify
