# Wildmere — development log

## Current phase
Phase 8 — Live with in-game auto-update. Latest: **v0.1.4** (2026-10-19, fullscreen fix, the first release delivered by auto-update).
Website https://wildmere-game.vercel.app (Vercel project `wildmere`, auto-deploys from `main`).

## Releases
- v0.1.0 (2026-10-19): first public release.
- v0.1.1: clouds are a single layer (one slab intersection instead of a density march, which looked
  multi-layered).
- v0.1.2: fixed floating props (PropRenderer kept the menu backdrop's chunks; VoxelWorld.OnDestroy now
  unloads them). `-propaudit [-viamenu]` exits 1 if any prop floats.
- v0.1.3: in-game auto-update (below). Players on 0.1.0–0.1.2 must download 0.1.3 by hand once.
- v0.1.4: fullscreen/borderless fill the screen. Before, they only switched the mode, so Unity kept the 1600x900
  window size and the OS letterboxed it on 16:10 MacBooks. `GraphicsApplier.ApplyDisplay` sets the display's own
  size (`Screen.mainWindowDisplayInfo`: 2940x1846 on the M4 Air, which is already below the notch; macOS keeps
  the 66 px notch strip black for every fullscreen app). The 3D view is capped at about 1920x1200 pixels at
  100% render scale (`kPixelBudget`; UI stays native). The A/B benchmark on the fanless Air was noisy
  (throttling, plus the user's game running): capped beat uncapped native, Ultra 52.6→74.8 fps and High
  33→48 / 44→71. Switching to windowed opens a centered window at ~3/4 of the display. Harness flags
  `-displaydiag` and `-displaycycle` (windowed → borderless → fullscreen, logs `WM_DISPLAYCYCLE`).
  `WM_MAC_OUT` / `WM_BUILD_DIR` build and package outside Build/mac (e.g. while the user has it open).

## Auto-update (v0.1.3)
- `Assets/Scripts/Core/Updater.cs`: checks
  `releases/latest/download/wildmere-update.json` 4 s after boot and every 6 h. The RSA-3072 PKCS#1 SHA-256
  signature (`.sig`) is checked against the public key compiled into the game. The download is staged in
  `persistentDataPath/Updates/<ver>`, then its size and SHA-256 are checked against the signed manifest.
  It installs on quit, or via "Restart to update" (title-screen card, Settings › Updates). A helper script
  (sh/PowerShell) waits for the game to exit, swaps the install with a `.previous` backup (rolling back on
  failure), and relaunches. `pending.json` drives the "Updated to X" report on the next boot.
- Signing: `tools/release/sign-update.sh` (called by publish.sh). The private key is in
  `.secrets/wildmere-update-key.pem` (gitignored, local only). The public key is
  `tools/release/update-public.pem`.
- Verified on macOS: `tools/release/test-updater-mac.sh`, 13/13 (good update and relaunch, tampered
  manifest, bad checksum, same version, install on quit with screenshot).
- Verified on Windows: `updater-windows.yml` run 39186895873 on windows-latest, 5/5 (download and verify,
  helper folder swap, relaunch reports 0.1.3 → 0.1.99, exe present, backup removed). The earlier rc.2
  failure was the test zip missing its top `Wildmere\` folder. The staging script now matches release
  packaging, and the updater accepts both layouts.
- `verify-release.yml` also verifies the manifest signature and checks version/file/sha/url against the
  downloaded zips.
- v0.1.3 release checks: local suites on the release build — self-test 19/19, QA 129/129, UI 42/42, EditMode
  84/84 (12 new `UpdaterTests`: version ordering, plus signature accept/reject with a vector signed by the
  release key). `verify-download.sh v0.1.3`: arm64 self-test 19/19 on the M4. `verify-release.yml` run
  39188540521: Windows x64, Apple Silicon, and Intel each 19/19, update manifest signed and matching.
  Harness runs (`-qatest/-uitest/-selftest/batchmode/...`) never check for updates on their own.
- Not covered: Intel Macs (only arm64 was run end to end; the code path is the same) and real
  user-installed Windows PCs (only GitHub's runner).

## Release verification (v0.1.0)
- Local: `tools/release/verify-download.sh v0.1.0` — checksums match the website's release.json; the downloaded
  Universal macOS app is ad-hoc signed, x86_64+arm64, self-test 19/19 on the M4.
- GitHub Actions `verify-release.yml` run 39175637868: Windows x64 self-test 19/19 + D3D11 (WARP) screenshot;
  macOS Apple Silicon 19/19; macOS Intel x86_64 19/19. Website marks Apple Silicon verified; Windows and Intel
  Macs are labelled experimental (automated checks only — no real Windows PC / Intel Mac was played on).
- Test suites on main: `-selftest` 19/19, `-qatest` 129/129, `-uitest` 42/42, EditMode 72/72.
- Perf (M4 Air, 1600x900, vsync off): Low 610 fps, Medium 342, High 206 (p99 7.1 ms), Ultra 138; default
  (High, vsync) locked 60 fps, 1% low 56.

## Completed (verified)
- Phase 1 foundation (lead): chunked voxel world (16x256x16), greedy mesher with flood-fill sky/block light
  + smooth AO, threaded generation/meshing, streaming/unloading, versioned saves (world.json + gzip block
  deltas), first-person controller (sprint/crouch-edge-safe/jump/swim/fall damage/air), mining with crack
  overlay + particles + drops, collision-safe placement, inventory (36 slots), shapeless crafting with
  workbench/furnace stations, item drops, viewmodel, day/night atmosphere, sky, water, post FX, graphics presets.
- Harness in the shipping binary: `-selftest` 19/19 PASS (gen, determinism, collision, move, jump, mining,
  pickup, placement, no-overlap placement, inventory, crafting, save, reload, persistence, streaming, memory,
  no console errors). `-shots`, `-bench`, `-camat/-camlook`, `-camprop`, `-camwild`, `-novsync`.
- Perf (M4 Air, 1600x900, High, view 10): vsync 60 fps locked; uncapped ~4.6 ms avg, p99 ~7 ms.
- Integrated: 54-layer procedural HD block textures + item icons (graphics agent); 33 Blender models
  (4 wildlife, 10 tools, 19 props, 33/33 import checks PASS); website + release tooling + CI (distribution agent).
- Props renderer (SRP-batched RenderMesh — instanced path lost material constants on Metal), prop collision.
- Wildlife: deer/doe/rabbits/songbirds, biome spawning, graze/wander/flee/perch/fly AI, procedural animation.
- Main-menu live world backdrop.

## Distribution status (2026-10-18)
- Public repo: https://github.com/tamoghnakabi-wq/wildmere (LFS for binaries).
- Website: Vercel project `wildmere` (team tamoghna1, root `website`, Git-connected, production = main),
  public at https://wildmere-game.vercel.app (wildmere.vercel.app belongs to an unrelated site). Production
  protection set to previews-only so the site is public. Shows "Builds coming soon" until release.json has assets.
- RC check (prerelease v0.1.0-rc.1 + verify-release.yml run 39130730211): Windows x64 self-test 19/19 and a
  rendered D3D11 (WARP) screenshot; macOS Apple Silicon 19/19; macOS Intel x86_64 19/19 (universal app).
- Release tooling fixes: accept LZ4 data.unity3d; grep -q SIGPIPE false failure under pipefail.

## Merged since last update
- World generation (biomes/rivers/lakes/caves/trees/props; 9 EditMode tests), audio bank (75 names, 265 files,
  CC0/PD, QA), UI Toolkit interface (42/42 -uitest incl. layout checks), AudioManager, app icon.
- Perf (M4 Air, 1600x900, vsync off): Low 480 fps, Medium 281, High 216, Ultra 160 avg; 4 worker threads
  remove the 60–130 ms streaming spikes seen with 6–8.

## In progress (agent worktrees under .claude/worktrees, branches worktree-agent-*)
- World generation (biomes, rivers, caves, trees, props placement) — fresh agent.
- Graphics phase 2 (lighting, water, clouds, weather system, post) — owns Assets/Shaders + Scripts/Rendering.
- UI/UX (menus, HUD, inventory/crafting UI, settings).
- Audio (CC0 sourcing/processing; then AudioManager subscribing to SoundHub + player/interaction events).

## Known issues
- Baseline generator is a placeholder until the worldgen branch merges (expect a conflict in Gen/TerrainGenerator.cs
  and Gen/PropCatalog.cs — take the agent's versions).
- Grass-top tint reads cyan under strong sky ambient (graphics agent assigned).
- DebugHud IMGUI is the only UI until the UI branch merges.
- No audio playback yet. Windows build not yet produced/validated (module installed).

## Next tasks (post-0.1.0 ideas)
- Hostile creatures/hunger (not in scope for 0.1), more recipes for gold/flint, dropped items persisted in saves.
- Real-hardware testing on Windows and Intel Macs; then flip `verified` in website/src/data/release.json.
- Optional code signing/notarization (needs an Apple Developer account) and Windows signing.
- Choose a licence for the code/assets (currently all rights reserved).

## Older next-task notes
1. Merge worldgen, graphics, UI, audio branches; rerun `tools/run.sh -selftest`, benchmarks per preset.
2. AudioManager (footsteps, mining, ambience by biome/time/weather, thunder, music).
3. QA agent pass; fix defects.
4. Build mac + Windows (`tools/build.sh all`), `tools/release/package.sh`, create public repo
   tamoghnakabi-wq/wildmere, `publish.sh`, real screenshots into website/media, new Vercel project (root `website`),
   run verify-release.yml (Windows/macOS runners).
