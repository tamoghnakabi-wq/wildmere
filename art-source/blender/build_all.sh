#!/bin/bash
# Regenerate every Wildmere model, icon, preview and models.json from source.
#   art-source/blender/build_all.sh [extra args passed to each script, e.g. --no-preview]
# Each script can also be run alone, optionally with --only Name1,Name2.
set -e
BLENDER="${BLENDER:-/Applications/Blender.app/Contents/MacOS/Blender}"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE/../.."
for s in build_wildlife build_tools build_props; do
  [ -f "$HERE/$s.py" ] || continue
  echo "== $s"
  "$BLENDER" -b --factory-startup --python "$HERE/$s.py" -- "$@" 2>&1 \
    | grep -E "^WM_|Error|Traceback|^  File" || true
done
python3 - <<'EOF'
import json
d = json.load(open("Assets/Resources/Models/models.json"))
mats = sorted({m for a in d["assets"].values() for m in a["materials"]})
print("WM_MANIFEST assets=%d materials=%s" % (len(d["assets"]), ",".join(mats)))
EOF
