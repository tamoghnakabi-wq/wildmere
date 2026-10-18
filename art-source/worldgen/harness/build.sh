#!/bin/bash
# Compile the generator sources + harness with Unity's bundled Mono/Roslyn and run it.
#   usage: art-source/worldgen/harness/build.sh [harness args...]
# Outputs land in art-source/worldgen/harness/bin (git-ignored).
set -e
H="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$H/../../.." && pwd)"
MB=/Applications/Unity/Hub/Editor/6000.3.22f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge
MONO="$MB/bin/mono"
mkdir -p "$H/bin"
"$MONO" "$MB/lib/mono/4.5/csc.exe" -nologo -optimize+ -unsafe -langversion:9 -nowarn:0649,0414,0169 \
  -r:System.IO.Compression.dll -out:"$H/bin/wg.exe" \
  "$H"/*.cs "$ROOT"/Assets/Scripts/World/Gen/*.cs \
  "$ROOT/Assets/Scripts/World/Chunk.cs" "$ROOT/Assets/Scripts/World/Blocks.cs" \
  "$ROOT/Assets/Scripts/World/IWorldGenerator.cs" "$ROOT/Assets/Tests/EditMode/WorldGenChecks.cs"
if [ "$#" -gt 0 ]; then exec "$MONO" "$H/bin/wg.exe" "$@"; fi
