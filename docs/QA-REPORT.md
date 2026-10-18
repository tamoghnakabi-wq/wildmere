# Wildmere QA report

Date: 2026-10-19. Branch `worktree-agent-qa` (from `main` @ 1d647ea; no game code changed by QA).
Machine: MacBook Air M4 (4P+6E), 16 GB, macOS 27.0.1, Metal. Unity 6000.3.22f1, URP 17.3.
Build: `tools/build.sh mac` (Universal, Release) with the new QA harness compiled in.

## What was added

| Path | What |
|---|---|
| `Assets/Scripts/QA/QaHarness.cs` | Runtime harness in the shipping binary, `Wildmere.QA`, auto-started only with `-qatest` (`[RuntimeInitializeOnLoadMethod]` + `Cli.Has("qatest")`). Modes: `-qatest` (functional), `-qatest soak -qasec N`, `-qatest idle -qaphase N`. Extra flags: `-qaseed`, `-qaonly group,group`, `-keepworld`. Prints `WM_QA PASS/FAIL <name> <detail>` and `WM_QA PASS/FAIL summary`, exit code 0/1. It creates its own `qatest-*` world (deleted at the end), backs up `settings.json` (also to `settings.json.qa-backup` on disk) and restores it, and never locks the cursor in windowed runs. |
| `Assets/Tests/EditMode/QaInventoryCraftingTests.cs` | 31 EditMode tests: Inventory, Crafting, ItemDb. |
| `Assets/Tests/EditMode/QaWorldTests.cs` | 32 EditMode tests: Json, WorldSave, ChunkMesher (face counts, culling across chunk borders, greedy merge, winding, water, leaves, furnace facing), VoxelPhysics. |
| `qa/run_monitored.py` | Launches the player and samples CPU (exact, from cumulative CPU time; 100 % = one core) and RSS from outside, tagged with the harness phase. Swaps in a QA `settings.json` (`--settings defaults|user-windowed|keep`) and restores the real one afterwards. |
| `qa/soak_report.py` | Turns `WM_QA_SOAK` lines into a CSV and flags growth. |
| `qa/results/` | Logs, CSVs and test XML referenced below. |

Everything lives in tracked files (no worktree-only state): after merging, `-qatest` runs from `main` with the commands below.
The functional run needs a normal (saving) world, so do not pass `-nosave`; it deletes its own `qatest-*` world at the end.

How to run:

```
tools/unity.sh - -runTests -testPlatform EditMode -testResults $PWD/TestResults.xml   # add -testCategory "!KnownDefect" for a green CI run
tools/build.sh mac
WM_RUNLOG=/tmp/qa.log tools/run.sh -batchmode -qatest            # ~4.5 min, functional
python3 qa/run_monitored.py --out qa/results/idle --settings defaults -- -qatest idle -nosave
python3 qa/run_monitored.py --out qa/results/soak --settings defaults --interval 10 -- -qatest soak -qasec 600 -nosave
python3 qa/soak_report.py qa/results/soak.log
```

## Results at a glance

| Suite | Result |
|---|---|
| EditMode | 72 tests (63 new QA + 9 existing WorldGen): 70 pass, 2 fail — both new tests tagged `KnownDefect` (D10, D11) that turn green when those fixes land. `qa/results/editmode-TestResults.xml` |
| `-qatest` functional (run 3, batch mode, seed 20261019) | 129 checks: **117 pass, 12 fail**; all 12 failures are real defects listed below (runs 1–2 also exposed 3 harness bugs, fixed). 0 console errors/exceptions. 261 s. `qa/results/qatest-run3.log` |
| `-qatest -qaonly settings -preset low` | proves D4 (CLI overrides persisted). `qa/results/qatest-settings-cli.log` |
| Idle CPU (windowed, visible) | 41–55 % of one core with default settings, 50–74 % with the user's Ultra/vsync-off settings. Never above one core. See "Idle cost". |
| 10-minute soak (windowed) | 0 errors; no leak (meshes/materials/columns flat, RSS 673→491 MB); fps drops to 43–55 in dense forest on High; 200–360 ms hitches in 6/20 windows. |

Verified-good highlights (all through the real systems, `qatest-run3.log`):
* All 40 breakable block types mined with the right tool: break time matched `ItemDb.BreakTime` to the frame (e.g. stone/iron pickaxe 0.38 s, ores 0.75 s), correct drop picked up, tool durability −1 (none for plants). Bedrock unbreakable. Wrong tool/tier: stone by hand 7.50 s and no drop, iron ore with wooden pickaxe 7.50 s no drop, gold with stone pickaxe 3.75 s no drop; tools still wear; a tool at 1 durability breaks and raises `ToolBroke`.
* Placement: into water, replacing tall grass/fern, flowers need soil, torches need a floor and are rejected on leaves/walls, never inside the player (feet, head, crouched head), never inside a deer, reach 4.5 m yes / 5.5 m no, furnace faces the player for all 4 directions and keeps its facing through unload and save/reload.
* All 23 recipes craft exactly in the world with stations placed; station recipes gated without stations; full-inventory crafting rolls back exactly; partial-fit rollback exact.
* Drops merge (stackables only), rest on the floor, are picked up, stay put when the inventory is full, partial pickup fills the last room, despawn at 300 s.
* Crouch-walking never leaves a ledge (4 headings incl. into a corner while sprint is held); swimming up out of a 3-deep pool onto a 1-block ledge in 1.3 s; jump 1.34 blocks; walk 4.30 m/s.
* Water landing no damage; drowning (air 0 then 2 HP/s); air refills; death raises `Died`, shows the death screen, captures the mouse, blocks mining; respawn restores 20 HP/10 air at the spawn column surface, input back; respawn works when the spawn column is unloaded (900 m away).
* Pause freezes player and time of day; resume restores time scale and closes the pause screen.
* Settings save/load round trip (incl. a rebinding), reset bindings, corrupt file falls back to defaults; switching Low/Ultra/Medium/High at runtime: no errors, view distance applied, settles in 1–3 s.
* Flying at 21.8 m/s for 20 s: the player's own 3×3 chunks were ready 100 % of frames. Loaded columns bounded (749 at view 14, bound ~1026).
* 359 scattered edits in 139 chunks (3 clusters up to 640 m apart, mostly unloaded at save time) survived unload/regenerate before saving and save → quit → reload; inventory slots/counts/durability, selected slot, look direction, time of day (0.830), health, and position under a roof all persisted.
* 240 trees in 81 chunks: no floating trees (connected-component check). Props are removed when their support block is mined.

## Defects

Severity: High = data loss/crash/blocks play; Medium = wrong gameplay or visible bug players will hit; Low = minor/edge/cosmetic.
No High-severity defect was found.

### D1 — Medium — Torches and plants popped by removing their support vanish (item loss)
* Owner: Lead (`Assets/Scripts/World/VoxelWorld.cs`).
* Repro: place a torch on dirt, mine the dirt. The torch disappears and no torch item drops (same for flowers on grass).
* Evidence: `WM_QA FAIL popped_torch_drops_item torch in inv=0 drops=0 dirt=1`, `WM_QA FAIL popped_flower_drops_item above=Air flowers=0` (every run).
* Cause: `VoxelWorld.SetBlock` (lines 201–206) clears the supported block with `SetBlock(x, y + 1, z, B.Air)` but nothing spawns its drop (`BlockInteraction.Break` only drops the block that was mined).
* Fix (VoxelWorld.cs ~205):
```csharp
-                if (Blocks.Defs[above] != null && Blocks.Defs[above].NeedsSupport) SetBlock(x, y + 1, z, B.Air);
+                if (Blocks.Defs[above] != null && Blocks.Defs[above].NeedsSupport)
+                {
+                    SetBlock(x, y + 1, z, B.Air);
+                    int drop = Items.ItemDb.DropOf(Blocks.Defs[above], null, sPopRng);   // torch/flower drop themselves, tall grass nothing
+                    if (drop != 0) Items.ItemDrop.Spawn(Items.ItemStack.Of(drop, 1), new Vector3(x + 0.5f, y + 1.3f, z + 0.5f), new Vector3(0f, 2f, 0f));
+                }
```
  plus `static readonly System.Random sPopRng = new System.Random();` in `VoxelWorld`. (`ItemDrop` already lives in the same assembly.)

### D2 — Medium — A ring of chunks at the view edge is never meshed (permanent holes)
* Owner: Lead (`Assets/Scripts/World/VoxelWorld.cs:232`).
* Repro: any world; wait for streaming to settle; compare the in-view set (`dx²+dz² <= VD²+VD`, the rule `VoxelWorld.Update` uses) with meshed columns.
* Evidence: `WM_QA FAIL all_in_view_meshed_at_rest view=14 unmeshed=36/665 (-3,-14) (3,-14) (-6,-13) ...`, same after a 437 m flight (17) and a 2.5 km teleport (36); preset switching: Low 8–16/137, Medium 0/225, High 0/349 at rest in that run, Ultra 36/665. Offline enumeration of the two ring formulas: never-meshable in-view chunks = 12 (VD 4), 16 (VD 6, Low), 4 (VD 8, Medium), 16 (VD 10, High), 36 (VD 14, Ultra). The nearest ones start ~80 m (Low), ~113 m (Medium), ~143 m (High) from the player, inside the terrain fade band (0.72–0.97 × view distance), so they show as notches/sky gaps in the faded horizon rather than as holes at your feet.
* Cause: generation ring radius is `ViewDistance + 1` but meshing a column needs all 8 neighbours generated; for in-view columns near the diagonal edge some neighbours fall outside the generation ring, so `TryQueueMesh` always returns false for them.
* Fix: `int genRadius = ViewDistance + 2;` (line 232). Verified offline for VD 4..20: 0 never-meshable chunks, and the larger ring still lies inside the unload radius (`ViewDistance + 3`), so nothing thrashes. Cost: ~+15 % generated (not meshed) columns.

### D3 — Medium — Saving while dead reloads a living player with 0 HP
* Owner: Lead (`Assets/Scripts/Core/GameRoot.cs` `SaveWorld`).
* Repro: die (death screen up), press "Save & Quit to Title" (or wait for the 120 s autosave), load the world.
* Evidence: `WM_QA FAIL save_while_dead_reloads_alive_state diedBeforeSave=True savedHealth=0 afterReload hp=0 dead=False deathScreen=False`. The player is alive with 0 hearts and no death screen; the next hit of any size kills, and the HUD shows an empty health bar until regeneration.
* Cause: `SaveWorld` writes `Player.Health` (0) and the death position; `LoadRoutine` restores health but never the `Dead` state.
* Fix (GameRoot.cs ~260): save a dead player as already respawned:
```csharp
-                var p = Player.transform.position;
+                bool dead = Player.Dead;
+                var p = dead ? Player.SpawnPoint : Player.transform.position;   // LoadRoutine lifts it to the surface if needed
 ...
-                    { "health", (double)Player.Health }, { "air", (double)Player.Air }, { "flying", Player.Flying },
+                    { "health", dead ? (double)PlayerController.MaxHealth : (double)Player.Health },
+                    { "air", dead ? (double)PlayerController.MaxAir : (double)Player.Air }, { "flying", Player.Flying },
```

### D4 — Medium — Command-line overrides are written into the player's settings.json
* Owner: Lead (`Assets/Scripts/Core/GameRoot.cs` Awake, `Assets/Scripts/Core/GameSettings.cs`).
* Repro: run with `-preset low` (or `-viewdist`, `-novsync`), then anything that calls `GameSettings.Save()` (Settings screen Apply, the `-uitest` harness's `RestoreSettings`).
* Evidence: `WM_QA FAIL cli_overrides_not_persisted file before: preset=Ultra view=14 vsync=False -> after Save(): preset=Low view=6 vsync=False` (`qatest-settings-cli.log`; the QA harness restored the file afterwards, md5 unchanged). The real settings.json on this machine currently holds Preset Ultra, ViewDistance 14, VSync false, Fullscreen 2 (exclusive) — consistent with harness flags having leaked, though I cannot prove where it came from. Note Fullscreen 2 also overrides `-screen-fullscreen 0`, so harness runs on this machine go exclusive-fullscreen unless settings are swapped (my monitor script does that).
* Cause: `GameRoot.Awake` applies `-preset/-viewdist/-novsync` to `GameSettings.Current`, which is the object `Save()` serialises.
* Fix (smallest): make settings session-only when overrides are present.
```csharp
// GameSettings.cs
+        /// <summary>True when command-line flags changed settings for this session: never write them to disk.</summary>
+        public static bool SessionOnly;
         public static void Save()
         {
-            try { File.WriteAllText(FilePath, JsonUtility.ToJson(Current, true)); }
+            if (!SessionOnly)
+                try { File.WriteAllText(FilePath, JsonUtility.ToJson(Current, true)); }
...
// GameRoot.Awake, after the overrides
+            GameSettings.SessionOnly = preset != null || Cli.Has("viewdist") || Cli.Has("novsync");
```

### D5 — Medium — Fall damage is under-counted by one point at random (frame-timing dependent)
* Owner: Lead (`Assets/Scripts/Player/PlayerController.cs:261-265`).
* Repro: drop from exactly 4, 6 or 10 blocks onto stone (the harness teleports to `floorTop + h`).
* Evidence (three runs): `3m:0 3.5m:0 4m:1 4m:0 4m:1 5m:2 5m:2 5m:2 6m:2 10m:6` — expected `floor(h-3)`: 4 m→1 (got 0 in 1 of 3 trials), 6 m→3 (got 2), 10 m→6 instead of 7.
* Cause: `Grounded` is detected by a probe up to 0.05 m below the feet, so the landing frame often fires while the feet are still a few cm above the floor; `fall = mFallStartY - pos.y` is then slightly below the integer and `Mathf.Floor(fall - 3f)` drops a point. The next frame `mFallStartY` is reset, so the missing centimetres are never counted.
* Fix (one line, PlayerController.cs:265): absorb the probe depth.
```csharp
-                if (fall > 3.4f && Submerged < 0.3f) Hurt(Mathf.Floor(fall - 3f), "fall");
+                if (fall > 3.4f && Submerged < 0.3f) Hurt(Mathf.Floor(fall - 3f + 0.06f), "fall");   // landing is detected up to 0.05 m early
```

### D6 — Low — A torch can be placed into a water cell
* Owner: Lead (`Assets/Scripts/Player/BlockInteraction.cs` `TryUse`).
* Repro: aim through shallow water at the floor below and place a torch: the water block becomes a torch (dry hole in the lake, lit underwater). Flowers would do the same on a soil floor.
* Evidence: `WM_QA FAIL torch_not_placed_in_water cell=Torch`.
* Fix (BlockInteraction.cs, right before `if (def.NeedsSupport)` at line 203): `if (existing == B.Water && def.NeedsSupport) return false;`

### D7 — Low — Crafting station range is a ±5 box, not the 4.5 m `StationRange`
* Owner: Gameplay (`Assets/Scripts/Items/Crafting.cs:91-101`).
* Evidence: `WM_QA FAIL station_range_is_radius workbench 7.1 m away diagonally counts as near=True (StationRange=4.5)`; 4 blocks yes / 6 blocks no along an axis pass.
* Fix (inside the loop, before reading the block):
```csharp
+                        float dx = x + 0.5f - p.x, dy = y + 0.5f - (p.y + 0.9f), dz = z + 0.5f - p.z;
+                        if (dx * dx + dy * dy + dz * dz > StationRange * StationRange) continue;
                         byte b = w.GetBlock(x, y, z);
```

### D8 — Low — Wildlife groups can spawn on tree canopies
* Owner: Gameplay (`Assets/Scripts/Wildlife/WildlifeManager.cs:162`).
* Evidence: every run 1–2 of 13–14 walkers stood on leaves after 25 s, e.g. `Deer@(157.8, 89.0, 156.3) on leaves`, `Rabbit@(104.7, 99.0, 166.5) on leaves`.
* Cause: `TrySpawn` rejects leaves for the group centre, but each group member uses `SurfaceY(gx, gz)` and only rejects water.
* Fix: `if (gy < 1 || w.GetBlock(gx, gy, gz) == B.Water || World.Blocks.IsLeaves(w.GetBlock(gx, gy, gz))) continue;`

### D9 — Low — Deer bodies clip into adjacent blocks
* Owner: Gameplay (`Assets/Scripts/Wildlife/Animal.cs` `Walkable`/`MoveWalker`).
* Evidence: `Deer@(82.15, 112.00, 168.05) overlaps Grass Block(81,112,168)` (same deer in all three runs; deterministic seed): a 0.7 m wide deer standing 0.15 m inside a one-block step.
* Cause: `Walkable` tests only the column under the animal's centre; `HalfWidth` (0.35 m deer) is ignored, so the body slides into neighbouring blocks.
* Fix: test the footprint corners, e.g. add
```csharp
bool FootprintClear(VoxelWorld w, Vector3 p, int y)
{
    float r = HalfWidth * 0.9f;
    for (int i = 0; i < 4; i++)
    {
        int x = Mathf.FloorToInt(p.x + ((i & 1) == 0 ? -r : r)), z = Mathf.FloorToInt(p.z + ((i & 2) == 0 ? -r : r));
        for (int k = 0; k < Mathf.CeilToInt(Height); k++) { byte b = w.GetBlock(x, y + k, z); if (Blocks.IsSolid[b] || b == B.Water) return false; }
    }
    return true;
}
```
  and in `MoveWalker` accept a candidate `ny` only when `Walkable(w, nx, ny, nz) && FootprintClear(w, next, ny)`.

### D10 — Low — `Inventory.Add` raises `Changed` even when nothing was added
* Owner: Gameplay (`Assets/Scripts/Items/Inventory.cs:64`) and Lead (`Assets/Scripts/Player/BlockInteraction.cs:127`).
* Evidence: `Inventory.Changed fired 221 times in 1.0s (221 frames) while standing on a drop with a full inventory`; each event refreshes the HUD hotbar and the inventory screen including `RefreshRecipes` (bound even while closed). Measured cost in batch mode is negligible (4.31 vs 4.26 ms per frame), so this is hygiene, not a performance problem today. EditMode `Add_ThatAddsNothing_DoesNotRaiseChanged` (KnownDefect) fails.
* Fix: in `Add`, remember `int start = count;` and call `Notify()` only `if (count != start)`; and in BlockInteraction line 127 `if (dist < 1.1f && inv.CanFit(d.Stack.Id, 1)) d.TryCollect(inv);`.

### D11 — Low — `Json.Write` emits `NaN`/`Infinity`, which `Json.Parse` rejects
* Owner: Lead (`Assets/Scripts/Core/Json.cs:118-119`).
* Evidence: EditMode `NonFiniteNumbers_ProduceParseableJson` (KnownDefect): `{"x":NaN,"y":Infinity}` → `FormatException: unexpected 'N'`. A non-finite player coordinate or time would make `world.json` unreadable (the world then disappears from the list, `TryLoadMeta` returns null). Not observed in play.
* Fix:
```csharp
-                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
-                case float f: sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
+                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture)); break;
+                case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : ((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
```

### D12 — Low — Menu and pause screens burn as much CPU as gameplay
* Owner: Lead (`GameRoot.SetState`) / UI.
* Evidence: see "Idle cost": paused 54.7 % of a core vs 40.9 % standing in the world (defaults, vsync 60); 74 % with the user's vsync-off settings. Not above one core, but a laptop sitting on the pause or title screen keeps the CPU and GPU busy.
* Suggested fix: render menus at half rate, e.g. in `GameRoot.SetState`:
  `UnityEngine.Rendering.OnDemandRendering.renderFrameInterval = (s == GameState.Paused || s == GameState.MainMenu) ? 2 : 1;`
  The cause of paused > playing was not isolated (likely the full-screen translucent UI Toolkit panels redrawn every frame).

### Code-review findings (not reproduced at runtime)
* R1 Low — `WorldSave.WriteAtomic` (WorldSave.cs:169-173) deletes `world.json` before moving the `.tmp` in; a crash in between leaves no `world.json`, so the world silently vanishes from the list. Use `if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);`.
* R2 Low (Windows, unverified) — `WorldSave.Slug` lets reserved device names through (`con`, `aux`, `nul`, `prn`, `com1`…`lpt9`); `Directory.CreateDirectory` fails for those on Windows, so "New World" named "Con" would throw. Prefix such slugs (e.g. `world-con`).
* R3 Low — with `-nosave`, `VoxelWorld.Save` is null, so player edits are lost when a chunk unloads and regenerates (harness-only flag; worth a comment).
* R4 Low — furnace facing is stored only when `def.LFront != def.LSide` (resolved texture layers). In `-nographics` runs (CI self-test) textures don't load, so facing is never recorded. Compare the layer names instead: `def.Front != def.Side` (BlockInteraction.cs `TryUse`, ChunkMesher `LayerFor`).
* R5 Info — Gold Ingot and Flint have no consumer recipe (dead-end items).
* R6 Info — dropped items are not saved; they vanish on Save & Quit (`CloseWorld` destroys them).

## Long-run stability (10-minute soak)

`python3 qa/run_monitored.py --out qa/results/soak --settings defaults --interval 10 -- -qatest soak -qasec 600 -nosave`
(windowed 1600×900, default settings: High, view 10, vsync 60). The harness cycled 30 s walk (sprint + jumps) → 30 s fly
(11–22 m/s, ~2.5 km covered in total) → 30 s mine/place (iron pickaxe, cobblestone), sampling every 30 s; the external
monitor sampled RSS/CPU every 10 s. Files: `qa/results/soak.log`, `soak-samples.csv` (harness), `soak.csv` (RSS/CPU).

| t (s) | managed MB | Unity alloc MB | reserved MB | meshes | GameObjects | columns loaded / meshed | vertices | avg / p99 / max frame ms |
|---|---|---|---|---|---|---|---|---|
| 60 | 73.8 | 246 | 376 | 570 | 724 | 466 / 411 | 1.58 M | 17.4 / 29.7 / 243 |
| 150 | 80.4 | 242 | 376 | 573 | 645 | 471 / 411 | 1.42 M | 16.9 / 26.6 / 34 |
| 270 | 90.7 | 370 | 468 | 574 | 1035 | 472 / 412 | 4.29 M | 20.2 / 33.8 / 50 |
| 360 | 89.5 | 389 | 503 | 575 | 1063 | 472 / 420 | 3.88 M | 23.3 / 41.0 / 50 |
| 510 | 97.6 | 299 | 480 | 579 | 653 | 467 / 411 | 1.42 M | 16.9 / 26.9 / 34 |
| 600 | 100.6 | 440 | 528 | 581 | 653 | 491 / 433 | 4.36 M | 17.8 / 33.5 / 47 |

Process RSS (external): 673 MB at 20 s, 702 MB peak at 260 s, 491 MB at 560 s — no upward trend.
CPU while playing: 52–67 % of one core (median per phase). 0 console errors/exceptions (`WM_QA PASS soak_no_console_errors`).

Assessment:
* **No leak found.** Mesh count (570→581), material count (134 constant), loaded/meshed columns (~470/~415) and drops (≤5) are flat; GameObjects follow the wildlife count. Unity-allocated memory tracks the vertex count of the area being viewed (1.4 M verts ≈ 240–300 MB, 4.3 M verts ≈ 370–440 MB) and falls back when the player leaves dense areas. At equal vertex load it rose from 242 MB (t=150) to 299 MB (t=510): allocator high-water/pool retention, not unbounded growth; RSS fell over the same period. Managed heap drifted 74→100 MB with a GC sawtooth (88 gen-0 collections in 10 min); worth re-checking with a 30-minute soak.
* **Frame rate drops in dense areas (observation, Medium for the perf budget):** while 3.3–4.4 M vertices were in view (dense forest, t≈240–390 s) the average frame was 18–23 ms (43–55 fps, p99 41 ms) on High at 1600×900 with vsync; elsewhere it held 60 fps (16.8–17.1 ms). The "~4.6 ms uncapped" figure in DEVELOPMENT_LOG.md was measured on a lighter path.
* **Hitches:** worst frames per 30 s window were 200–360 ms in 6 of 20 windows (mostly fly phases entering new terrain, and the first minute); 35–50 ms otherwise. Caveat: in this build the frame after each 30 s sample (which calls `Resources.FindObjectsOfTypeAll`) was counted in the next window; windows whose max is 34 ms show that cost is small, so the 200+ ms spikes are most likely real streaming/GC hitches. The harness now excludes that frame (`prevSampleFrameMs` in the line) for the next run; attribute spikes with `-bench 60 -benchspeed 22` (prints `WM_SPIKE` with gen/mesh queue sizes), which I did not get to.

## Idle cost (CPU % of one core; window visible, 1600×900, M4)

Measured with `qa/run_monitored.py` (CPU from cumulative CPU-time deltas every 2 s; first 3 samples of each phase skipped).

| Phase | Defaults (High, vsync on → 59.7 fps) | User settings, windowed (Ultra, vsync off, cap 0) |
|---|---|---|
| Main menu (live backdrop) | median 43 %, mean 46 % | median 50 %, mean 77 % (incl. backdrop generation burst up to 377 %), 80 fps |
| In world, standing still | median 41 %, mean 40 % | median 74 %, mean 76 %, 55 fps (GPU-bound) |
| Paused | median 55 %, mean 53 % (56.7 fps) | median 74 %, mean 72 %, 54 fps |
| Back on the menu | median 42 % | median 62 % |

RSS: 400–700 MB. Nothing exceeds one full core. Files: `qa/results/idle-defaults*`, `qa/results/idle-userwindowed*`.

## Exploratory screenshots

Not done — the coordinator asked to converge before the screenshot pass. Visual checks that were covered by the
harness instead: chunk-border culling and face winding (EditMode mesher tests), view-edge holes (D2, measured), floating trees
(0 of 240), props removed with their support, animals inside blocks / on canopies (D8, D9). Still unchecked by eye: z-fighting,
missing textures/pink materials, lighting seams at night.

## What I could not verify
* Windows: no Windows build or machine was used. Everything above is macOS/Metal (M4) only; R2 is code review.
* Real input devices: movement/mining/placing were driven through `ScriptedMove/ScriptedAttack/ScriptedUse`; mouse look, key rebinding at runtime through the UI, and the inventory drag-and-drop UI were not exercised (the existing `-uitest` covers parts of the UI).
* Audio output (only that no errors were logged).
* GPU memory: `Profiler.GetAllocatedMemoryForGraphicsDriver` returns 0 in release players; memory growth was judged from managed/Unity-allocated counters, object counts and process RSS.
* Functional runs were in `-batchmode` (no cursor capture, uncapped frame rate); soak, idle and screenshots were windowed.
* Ultra preset was the configuration in the user's settings during functional runs; hole counts for Low/Medium/High come from the runtime preset switch plus offline enumeration.
