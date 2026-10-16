# Wildmere — architecture and team contracts

**Wildmere** is an original voxel survival sandbox ("Shape the wild.") built in Unity
6000.3.22f1 with URP 17.3 for macOS (Apple Silicon first) and Windows x64. It is
Minecraft-*inspired*: no Mojang names, assets, textures, sounds, logos or code.

Main checkout: `/Users/tamoghnakabi/studioproject/wildmere` (branch `main`).
Version lives in `VERSION` (single line, semver).

## World constants (shared by every module)

| Constant | Value |
|---|---|
| 1 Unity unit | 1 block = 1 metre |
| World height | 256 blocks (y = 0..255), bedrock at y = 0 |
| Sea level | y = 62 (water fills air at y <= 62 in open terrain) |
| Chunk column | 16 x 256 x 16, stored as `byte[]` block ids, index = `x + z*16 + y*256` |
| Mesh section | 16 x 16 x 16 (16 per column) |
| Up axis | +Y; "forward" for models is +Z (Unity space) |

## Block ids (`Assets/Scripts/World/Blocks.cs` is authoritative)

```
0 Air        1 Stone      2 Dirt       3 Grass      4 Sand       5 Sandstone
6 Gravel     7 Clay       8 Snow       9 Ice        10 Bedrock   11 Water
12 Cobblestone 13 MossyCobblestone 14 Planks 15 Glass
16 LogOak 17 LogBirch 18 LogSpruce 19 LogDark
20 LeavesOak 21 LeavesBirch 22 LeavesSpruce 23 LeavesDark
24 CoalOre 25 IronOre 26 GoldOre 27 Cactus 28 Workbench 29 Furnace 30 Torch
31 TallGrass 32 Fern 33 FlowerRed 34 FlowerYellow 35 FlowerBlue 36 FlowerWhite
37 DeadBush 38 MushroomRed 39 MushroomBrown 40 Podzol 41 SnowyGrass 42 GlowCap
```

## Module ownership (prevents conflicting edits)

Each agent edits ONLY the paths it owns. Need a change elsewhere? Describe it in
your final report (file, reason, exact suggested diff) and the lead integrates it.
Shared folder `.meta` files are pre-created in `main` — never regenerate them.

| Owner | Paths |
|---|---|
| Lead / integrator | `Assets/Scripts/Core/**`, `Assets/Scripts/World/*.cs` (not `Gen/`), `Assets/Scripts/Player/**`, `Assets/Scenes/**`, `Assets/Editor/WildmereSetup.cs`, `Assets/Editor/BuildScript.cs`, `Assets/Scripts/Wildmere.asmdef`, `ProjectSettings/**`, `Packages/**`, `tools/*.sh`, `DEVELOPMENT_LOG.md`, `docs/ARCHITECTURE.md`, `CLAUDE.md` |
| Graphics (textures, shaders, lighting) | `art-source/textures/**`, `Assets/Resources/Blocks/**`, `Assets/Resources/Textures/**`, `Assets/Shaders/**`, `Assets/Scripts/Rendering/**`, `Assets/Editor/Graphics*.cs`, `Assets/Editor/BlockTexturePostprocessor.cs` |
| Blender assets | `art-source/blender/**`, `Assets/Art/Models/**`, `Assets/Art/Icons/**`, `Assets/Resources/Models/**`, `Assets/Editor/Model*.cs`, `BLENDER_ASSETS.md` |
| Gameplay + audio | `art-source/audio/**`, `Assets/Resources/Audio/**`, `AUDIO_CREDITS.md`, `Assets/Scripts/Audio/**`, `Assets/Scripts/Items/**`, `Assets/Scripts/Wildlife/**` |
| World generation | `Assets/Scripts/World/Gen/**`, `Assets/Tests/EditMode/WorldGen*.cs` |
| UI / UX | `Assets/Scripts/UI/**`, `Assets/UI/**`, `Assets/Resources/UI/**` |
| Distribution | `website/**`, `.github/**`, `tools/release/**`, `docs/RELEASING.md`, `docs/INSTALL.md` |
| QA | `Assets/Tests/**` (except WorldGen*), `qa/**`, `docs/QA*.md` |

## Running Unity (all agents)

* Always go through `tools/unity.sh <Class.Method>` (or `tools/unity.sh - <args>`).
  It holds a machine-wide 2-slot lock so parallel agents never run more than two
  Unity processes on the 16 GB MacBook Air. Never call the Unity binary directly
  and never open the Unity GUI.
* A worktree has no `Library/`. Before its first Unity run, clone the main one
  (APFS copy-on-write, instant, no disk cost):
  `cp -cR /Users/tamoghnakabi/studioproject/wildmere/Library ./Library` (if it exists).
* Compile check: `tools/unity.sh WildmereSetup.CompileCheck` (prints `WM_COMPILE_OK`).
* `grep "error CS"` the log; a run is only green when there are zero compiler errors.
* Runtime code is one assembly, `Wildmere` (`Assets/Scripts/Wildmere.asmdef`), namespace
  `Wildmere` (sub-namespaces per module, e.g. `Wildmere.World.Gen`, `Wildmere.UI`).
* Materials are built at runtime with `Shader.Find`; every custom shader must be added to
  the always-included list in `WildmereSetup` (tell the lead its exact name).
* Legacy input (`UnityEngine.Input`) is active; the Input System package is NOT installed.
* Log markers: lines that start with `WM_` are surfaced by tools/unity.sh and tools/run.sh.

## Asset contracts

### Block textures (Graphics) -> `Assets/Resources/Blocks/`
* 128 x 128 px per layer, seamlessly tileable, original procedural art (no external images).
* Packed sheets, 8 columns, row-major from the TOP-LEFT tile, padded to whole rows:
  `blocks_albedo.png` (sRGB RGBA; alpha = cutout coverage for plants/leaves/glass/torch),
  `blocks_normal.png` (tangent-space, OpenGL +Y), `blocks_mask.png` (linear RGBA:
  R = ambient occlusion, G = smoothness, B = height, A = biome-tint mask; 1 = multiply by
  biome colour), plus `blocks.json`:
  `{"tile":128,"columns":8,"rows":R,"layers":["grass_top",...],"flags":{"leaves_oak":["cutout","tint"],...}}`.
* Runtime loads `Resources.Load<Texture2DArray>("Blocks/blocks_albedo")` etc. and
  `Resources.Load<TextAsset>("Blocks/blocks")`; layer index = position in `layers`.
* Crack overlay `crack.png`: 8 stages, 8 x 1 grid of 128 px, alpha.
* Water: `Textures/water_normal_a.png`, `Textures/water_normal_b.png` (256 px tileable,
  OpenGL normals), `Textures/water_foam.png` (greyscale); sky noise `Textures/cloud_noise.png`.

### Models (Blender) -> `Assets/Resources/Models/<Category>/<Name>.fbx`
* They live under `Resources/` so the runtime can `Resources.Load<GameObject>("Models/<Category>/<Name>")`.
  Tool icons (transparent PNG renders) go to `Assets/Resources/Models/Icons/<name>.png`.
* Built by reproducible scripts in `art-source/blender/` (headless:
  `/Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup --python <script>`).
* Metres; 1 block = 1 m. Unity space: +Y up, +Z forward.
  - Props: origin at ground-contact centre. Animals: origin on the ground between the feet,
    facing +Z. Held tools: origin at the grip, long axis +Y, working edge toward +Z.
* Albedo in **vertex colours** (sRGB), one material per surface type named `M_<Surface>`
  (e.g. `M_Bark`, `M_Stone`, `M_Fur`, `M_Cap`, `M_Metal`, `M_Wood`). The runtime maps
  `M_<Surface>` names to its own shaders, so Blender material node setups are not exported.
* Animals: separate child objects with pivots at joints, named
  `Body, Neck, Head, EarL, EarR, LegFL, LegFR, LegBL, LegBR, Tail, WingL, WingR` (as
  applicable). Unity animates these procedurally.
* Budgets (triangles, LOD0): props <= 1500, animals <= 2500, birds <= 600, tools <= 800.
  Props/animals also ship an `_LOD1` child at ~40 %.
* `Assets/Resources/Models/models.json`: each asset's path, category, triangles, bounds (m),
  pivot notes, intended biome(s).

### Audio (Gameplay) -> `Assets/Resources/Audio/<group>/<name>_<nn>.wav`
* CC0 recordings only (CC-BY allowed only with attribution); provenance in `AUDIO_CREDITS.md`.
* 48 kHz 16-bit WAV. Mono for positional one-shots, stereo for ambience beds; ambience loops
  must loop seamlessly. Peak <= -1 dBFS, no DC, no clicks.
* `Assets/Resources/Audio/audio.json`: `{ "<group>/<name>": {"files": [...], "volume": 0.8, "loop": false} }`.

### Builds and the release (Lead + Distribution)
* `BuildScript.BuildMac` -> `Build/mac/Wildmere.app` (Universal: Apple Silicon + Intel,
  ad-hoc signed, NOT notarized). `BuildScript.BuildWindows` -> `Build/win/` (Wildmere.exe +
  `Wildmere_Data/`, `UnityPlayer.dll`, `MonoBleedingEdge/`, ...; ship the whole folder).
* Release assets: `Wildmere-<ver>-macOS.zip`, `Wildmere-<ver>-Windows-x64.zip`.
* Player harness flags (shipping binary): `-selftest` (scripted gameplay checks, prints
  `WM_SELFTEST ...`, exit code 0 = pass), `-shots <sec,sec,...> -shotdir <dir>`,
  `-quitafter <sec>`, `-bench <sec>` (prints `WM_PERF` lines), `-seed <n>`, `-world <name>`.
  `-batchmode -nographics -selftest` must work (CI on Windows/macOS runners).

## Git
* Agents work on their own worktree branch and commit there with clear messages; the lead
  merges. Commit author email: `252209537+tamoghnakabi-wq@users.noreply.github.com`.
* Never commit `Library/`, `Temp/`, `Build/`, `dist/`, downloads or caches.
