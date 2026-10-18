# Wildmere — development log

## Current phase
Phase 2–4 in parallel (world gen, graphics, UI, audio agents running in worktrees). Last update 2026-10-18.

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

## Next tasks
1. Merge worldgen, graphics, UI, audio branches; rerun `tools/run.sh -selftest`, benchmarks per preset.
2. AudioManager (footsteps, mining, ambience by biome/time/weather, thunder, music).
3. QA agent pass; fix defects.
4. Build mac + Windows (`tools/build.sh all`), `tools/release/package.sh`, create public repo
   tamoghnakabi-wq/wildmere, `publish.sh`, real screenshots into website/media, new Vercel project (root `website`),
   run verify-release.yml (Windows/macOS runners).
