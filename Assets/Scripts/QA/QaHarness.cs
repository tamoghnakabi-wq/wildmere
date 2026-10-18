using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.Player;
using Wildmere.World;

namespace Wildmere.QA
{
    /// <summary>
    /// QA harness in the shipping binary, started only with <c>-qatest</c>:
    ///   -qatest            functional checks through the real systems (WM_QA PASS/FAIL lines, exit 0/1)
    ///   -qatest soak       long run (walk/fly/mine) with memory/frame samples every 30 s (-qasec, default 600)
    ///   -qatest idle       menu / in-world / paused phases for external CPU sampling (-qaphase seconds, default 40)
    /// Optional: -qaseed N, -qaonly name1,name2 (run only those functional groups), -keepworld.
    /// It never touches the player's real worlds: it creates its own (deleted at the end) and restores settings.json.
    /// </summary>
    public sealed class QaHarness : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Cli.Has("qatest")) return;
            var go = new GameObject("QaHarness");
            DontDestroyOnLoad(go);
            go.AddComponent<QaHarness>();
        }

        int mPass, mFail;
        readonly List<string> mFailed = new List<string>();
        readonly List<string> mErrors = new List<string>();
        int mWarnings;
        string mWorldDir;
        string mSettingsPath;
        byte[] mSettingsBackup;
        bool mSettingsRestored;
        HashSet<string> mOnly;

        static GameRoot R => GameRoot.Instance;
        static VoxelWorld W => VoxelWorld.Instance;
        static PlayerController P => GameRoot.Instance != null ? GameRoot.Instance.Player : null;
        static BlockInteraction BI => GameRoot.Instance != null ? GameRoot.Instance.Interaction : null;

        void OnEnable() => Application.logMessageReceived += OnLog;
        void OnDisable() => Application.logMessageReceived -= OnLog;

        void OnLog(string msg, string stack, LogType type)
        {
            if (msg.StartsWith("WM_QA", StringComparison.Ordinal)) return;
            if (type == LogType.Warning) { mWarnings++; return; }
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                string first = stack != null ? stack.Split('\n')[0] : "";
                mErrors.Add($"{type}: {(msg.Length > 240 ? msg.Substring(0, 240) : msg)} @ {first}");
            }
        }

        void LateUpdate()
        {
            // Windowed QA runs must not grab the user's mouse.
            if (!Application.isBatchMode && Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        void OnApplicationQuit() => RestoreSettings();

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) mPass++; else { mFail++; mFailed.Add(name); }
            Debug.Log($"WM_QA {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        static void Info(string s) => Debug.Log("WM_QA_INFO " + s);

        bool Want(string group) => mOnly == null || mOnly.Contains(group);

        IEnumerator Start()
        {
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
            mSettingsPath = Path.Combine(Application.persistentDataPath, "settings.json");
            mSettingsBackup = File.Exists(mSettingsPath) ? File.ReadAllBytes(mSettingsPath) : null;
            if (mSettingsBackup != null) File.WriteAllBytes(mSettingsPath + ".qa-backup", mSettingsBackup); // survives a crash mid-run
            var only = Cli.Str("qaonly");
            if (only != null) mOnly = new HashSet<string>(only.Split(','), StringComparer.OrdinalIgnoreCase);

            float t0 = Time.realtimeSinceStartup;
            while ((R == null || R.State != GameState.MainMenu) && Time.realtimeSinceStartup - t0 < 60f) yield return null;
            Info($"boot state={R?.State} batch={Application.isBatchMode} gfx={SystemInfo.graphicsDeviceType} vsync={GameSettings.Current.VSync} cap={GameSettings.Current.FpsCap} preset={GameSettings.Current.Preset} view={GameSettings.Current.ViewDistance}");
            string mode = Cli.Str("qatest", "functional").ToLowerInvariant();
            if (mode == "soak") yield return Soak();
            else if (mode == "idle") yield return Idle();
            else yield return Functional();
        }

        // ================================================================== helpers

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static IEnumerator WaitPlaying(float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (R.State != GameState.Playing && Time.realtimeSinceStartup - t0 < timeout) yield return null;
        }

        static IEnumerator WaitArea(Vector3 p, int radius, float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (W != null && !W.ReadyAround(p, radius) && Time.realtimeSinceStartup - t0 < timeout) yield return null;
        }

        /// <summary>Wait until streaming is idle (no gen/mesh work) for a while.</summary>
        static IEnumerator Settle(float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            int quiet = 0;
            while (Time.realtimeSinceStartup - t0 < timeout)
            {
                var w = W;
                if (w == null) yield break;
                bool idle = w.GenInFlight == 0 && w.MeshInFlight == 0 && w.Pool.MainPending == 0;
                quiet = idle ? quiet + 1 : 0;
                if (quiet > 30 && Time.realtimeSinceStartup - t0 > 1f) yield break;
                yield return null;
            }
        }

        /// <summary>In-view chunks (same rule as VoxelWorld) that are not meshed at their current version.</summary>
        static List<Vector2Int> UnmeshedInView(out int total)
        {
            var miss = new List<Vector2Int>();
            var w = W;
            int vd = w.ViewDistance;
            int fcx = Mathf.FloorToInt(w.Focus.x) >> 4, fcz = Mathf.FloorToInt(w.Focus.z) >> 4;
            total = 0;
            for (int dz = -vd; dz <= vd; dz++)
                for (int dx = -vd; dx <= vd; dx++)
                {
                    if (dx * dx + dz * dz > vd * vd + vd) continue;
                    total++;
                    var c = w.GetChunk(fcx + dx, fcz + dz);
                    if (c == null || c.State != ChunkState.Generated || c.MeshedVersion < 0 || c.MeshedVersion != c.Version)
                        miss.Add(new Vector2Int(dx, dz));
                }
            return miss;
        }

        static string Fmt(List<Vector2Int> l, int max = 12) =>
            string.Join(" ", l.Take(max).Select(v => $"({v.x},{v.y})")) + (l.Count > max ? $" ...+{l.Count - max}" : "");

        static void Set(Vector3Int c, byte id, byte meta = 0) => W.SetBlock(c.x, c.y, c.z, id, meta);
        static byte Get(Vector3Int c) => W.GetBlock(c.x, c.y, c.z);

        static void Fill(int x0, int y0, int z0, int x1, int y1, int z1, byte id)
        {
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++) W.SetBlock(x, y, z, id);
        }

        static Vector3 Center(Vector3Int c) => c + new Vector3(0.5f, 0.5f, 0.5f);

        static void Face(Vector3 target)
        {
            var d = (target - P.EyePosition).normalized;
            P.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            P.Pitch = -Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        static IEnumerator Aim(Vector3 target)
        {
            Face(target);
            yield return null;
            Face(target); // eye height may have settled
            yield return null;
            yield return null;
        }

        static void Stand(Vector3Int floorBlock)
        {
            var p = P;
            p.Flying = false;
            p.ScriptedMove = Vector2.zero; p.ScriptedJump = false; p.ScriptedSprint = false; p.ScriptedCrouch = false;
            p.Teleport(new Vector3(floorBlock.x + 0.5f, floorBlock.y + 1.001f, floorBlock.z + 0.5f));
        }

        static IEnumerator ClearDrops()
        {
            foreach (var d in ItemDrop.All.ToArray()) if (d != null) Destroy(d.gameObject);
            yield return null;
        }

        static void Hold(int id, int count = -1, int durability = -1)
        {
            var inv = P.Inventory;
            inv.Clear();
            if (id != 0)
            {
                inv.Slots[0] = ItemStack.Of(id, count > 0 ? count : Inventory.MaxStack(id));
                if (durability >= 0) inv.Slots[0].Durability = durability;
            }
            inv.Selected = 0;
            inv.Notify();
        }

        float mMineTime;
        bool mMineBroke, mMineTargeted;

        IEnumerator MineAt(Vector3Int t, float timeout)
        {
            yield return Aim(Center(t));
            mMineTargeted = BI.Target.Hit && BI.Target.Block == t;
            byte id0 = Get(t);
            float t0 = Time.time;
            BI.ScriptedAttack = true;
            while (Get(t) == id0 && Time.time - t0 < timeout) yield return null;
            mMineTime = Time.time - t0;
            BI.ScriptedAttack = false;
            mMineBroke = Get(t) != id0;
            yield return null;
        }

        static IEnumerator Use()
        {
            BI.ScriptedUse = true;
            yield return null; yield return null;
            BI.ScriptedUse = false;
            yield return null;
        }

        void Restart(string why)
        {
            Info("restoring player state: " + why);
            var ui = UI.UiRoot.Instance;
            if (P != null && P.Dead) { if (ui != null && ui.Death != null) ui.Death.Respawn(); else P.Respawn(); }
            if (P != null) { P.Health = PlayerController.MaxHealth; P.Air = PlayerController.MaxAir; }
        }

        // ================================================================== functional

        Vector3Int mA;          // arena origin (platform block under the player's start cell)
        int Y0 => mA.y;
        Vector3Int mSpawnCol;

        IEnumerator Functional()
        {
            float tStart = Time.realtimeSinceStartup;
            long seed = Cli.Long("qaseed", 20261019);
            string name = "qatest-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            R.NewWorld(name, seed);
            yield return WaitPlaying(120f);
            Check("world_ready", R.State == GameState.Playing && W != null && P != null, $"t={Time.realtimeSinceStartup - tStart:F1}s chunks={W?.Loaded}");
            if (W == null || P == null) { yield return Finish(); yield break; }
            mWorldDir = R.Save != null ? R.Save.Dir : null;
            Check("world_saved_on_create", mWorldDir != null && File.Exists(Path.Combine(mWorldDir, "world.json")), mWorldDir);
            Check("starter_kit", P.Inventory.Count(B.Torch) == 4, $"torches={P.Inventory.Count(B.Torch)}");
            yield return Settle(30f);

            var sp = P.SpawnPoint;
            mSpawnCol = new Vector3Int(Mathf.FloorToInt(sp.x), 0, Mathf.FloorToInt(sp.z));
            Wildlife.WildlifeManager.Enabled = false;
            if (Wildlife.WildlifeManager.Instance != null) Wildlife.WildlifeManager.Instance.Clear();
            BI.ScriptedAttack = false; BI.ScriptedUse = false;
            P.ScriptedMove = Vector2.zero;

            // Arena: a floating stone platform 24 blocks east of spawn at y = 200 (never over the spawn column).
            mA = new Vector3Int(mSpawnCol.x + 24, 200, mSpawnCol.z);
            Fill(mA.x - 9, Y0, mA.z - 9, mA.x + 9, Y0, mA.z + 9, B.Stone);
            Stand(mA);
            yield return WaitArea(P.transform.position, 1, 20f);
            yield return new WaitForSeconds(0.5f);
            Check("arena_stand", P.Grounded && Mathf.Abs(P.transform.position.y - (Y0 + 1.001f)) < 0.05f, $"y={P.transform.position.y:F3}");

            var groups = new (string, Func<IEnumerator>)[]
            {
                ("mining", new Func<IEnumerator>(Mining)), ("tools", new Func<IEnumerator>(Tools)), ("placement", new Func<IEnumerator>(Placement)), ("crafting", new Func<IEnumerator>(CraftingChecks)),
                ("drops", new Func<IEnumerator>(Drops)), ("movement", new Func<IEnumerator>(Movement)), ("damage", new Func<IEnumerator>(Damage)), ("pause", new Func<IEnumerator>(PauseResume)),
                ("settings", new Func<IEnumerator>(SettingsChecks)), ("streaming", new Func<IEnumerator>(Streaming)), ("wildlife", new Func<IEnumerator>(WildlifeAndWorld)),
                ("persistence", new Func<IEnumerator>(Persistence)), ("respawnfar", new Func<IEnumerator>(RespawnFar)),
            };
            foreach (var (g, fn) in groups)
            {
                if (!Want(g)) continue;
                if (W == null || P == null) { Check("group_" + g, false, "world gone"); continue; }
                Info($"group {g} t={Time.realtimeSinceStartup - tStart:F0}s");
                int errs = mErrors.Count;
                yield return fn();
                BI.ScriptedAttack = false; BI.ScriptedUse = false;
                if (P != null) { P.ScriptedMove = Vector2.zero; P.ScriptedJump = false; P.ScriptedCrouch = false; P.ScriptedSprint = false; }
                if (mErrors.Count > errs) Info($"group {g} logged {mErrors.Count - errs} error(s); first: {mErrors[errs]}");
            }
            Info($"functional done in {Time.realtimeSinceStartup - tStart:F0}s");
            yield return Finish();
        }

        // ------------------------------------------------------------------ mining

        IEnumerator Mining()
        {
            var t = new Vector3Int(mA.x, Y0 + 2, mA.z + 2);
            var support = t + Vector3Int.down;
            var rng = new System.Random(1);
            int tested = 0, bad = 0;
            for (int id = 1; id < 256; id++)
            {
                if (!Blocks.Exists(id) || id == B.Water || id == B.Bedrock) continue;
                var def = Blocks.Get(id);
                int tool = def.Tool == ToolKind.Pickaxe ? ItemIds.PickaxeIron : def.Tool == ToolKind.Axe ? ItemIds.AxeIron : def.Tool == ToolKind.Shovel ? ItemIds.ShovelIron : 0;
                yield return ClearDrops();
                Stand(mA);
                Hold(tool);
                Set(support, B.Dirt);
                Set(t, (byte)id);
                yield return Frames(2);
                var held = ItemDb.Get(tool);
                float expect = ItemDb.BreakTime(def, held);
                int durBefore = P.Inventory.Slots[0].Durability;
                yield return MineAt(t, expect + 3f);
                yield return new WaitForSeconds(1.7f);
                int expectDrop = ItemDb.DropOf(def, held, rng);
                bool randomDrop = id == B.Gravel || Blocks.IsLeaves((byte)id);
                int got = expectDrop == 0 ? 0 : P.Inventory.Count(expectDrop);
                int extraSlots = P.Inventory.Slots.Count(s => !s.IsEmpty) - (tool != 0 ? 1 : 0);
                bool dropOk = randomDrop
                    ? (id == B.Gravel ? extraSlots == 1 : extraSlots <= 1)
                    : (expectDrop == 0 ? P.Inventory.Slots.Count(s => !s.IsEmpty) == (tool != 0 ? 1 : 0) : got == 1);
                bool timeOk = Mathf.Abs(mMineTime - expect) <= 0.12f + expect * 0.05f;
                bool durOk = tool == 0 || def.Hardness <= 0f ? P.Inventory.Slots[0].Durability == durBefore : P.Inventory.Slots[0].Durability == durBefore - 1;
                bool ok = mMineTargeted && mMineBroke && Get(t) == B.Air && dropOk && timeOk && durOk;
                tested++;
                if (!ok) bad++;
                Check($"mine_{def.Key}", ok,
                    $"tool={ItemDb.NameOf(tool)} targeted={mMineTargeted} broke={mMineBroke} t={mMineTime:F2}s expect={expect:F2}s drop={ItemDb.NameOf(expectDrop)} got={got} dur {durBefore}->{P.Inventory.Slots[0].Durability}");
                Set(t, B.Air);
            }
            Info($"mined {tested} block types, {bad} bad");

            // Bedrock: unbreakable.
            Hold(ItemIds.PickaxeIron);
            Set(t, B.Bedrock);
            yield return Frames(2);
            yield return MineAt(t, 3f);
            Check("bedrock_unbreakable", Get(t) == B.Bedrock && BI.MineProgress == 0f, $"block={Blocks.Get(Get(t)).Name}");
            Set(t, B.Air);
            Set(support, B.Air);
            yield return ClearDrops();
        }

        // ------------------------------------------------------------------ tools / tiers / wear

        IEnumerator Tools()
        {
            var t = new Vector3Int(mA.x, Y0 + 2, mA.z + 2);
            var support = t + Vector3Int.down;
            Stand(mA);
            Set(support, B.Dirt);

            IEnumerator Case(string name, byte block, int tool, float expect, int expectDrop)
            {
                yield return ClearDrops();
                Hold(tool);
                Set(t, block);
                yield return Frames(2);
                int dur0 = P.Inventory.Slots[0].Durability;
                yield return MineAt(t, expect + 3f);
                yield return new WaitForSeconds(1.7f);
                int extra = P.Inventory.Slots.Count(s => !s.IsEmpty) - (tool != 0 ? 1 : 0);
                bool dropOk = expectDrop == 0 ? extra == 0 : P.Inventory.Count(expectDrop) == 1;
                bool wear = tool == 0 || P.Inventory.Slots[0].Durability == dur0 - 1;
                Check(name, mMineBroke && Mathf.Abs(mMineTime - expect) <= 0.15f + expect * 0.05f && dropOk && wear,
                    $"t={mMineTime:F2}s expect={expect:F2}s extraItems={extra} dur {dur0}->{P.Inventory.Slots[0].Durability}");
                Set(t, B.Air);
            }

            yield return Case("stone_by_hand_slow_no_drop", B.Stone, 0, 7.5f, 0);
            yield return Case("iron_ore_wood_pickaxe_no_drop", B.IronOre, ItemIds.PickaxeWood, 7.5f, 0);
            yield return Case("gold_ore_stone_pickaxe_no_drop", B.GoldOre, ItemIds.PickaxeStone, 3.75f, 0);
            yield return Case("iron_ore_stone_pickaxe_drops", B.IronOre, ItemIds.PickaxeStone, 1.125f, ItemIds.RawIron);
            yield return Case("log_with_pickaxe_hand_speed", B.LogOak, ItemIds.PickaxeIron, 3f, B.LogOak);
            yield return Case("coal_wood_pickaxe", B.CoalOre, ItemIds.PickaxeWood, 2.25f, ItemIds.Coal);

            // Plants never wear a tool.
            yield return ClearDrops();
            Hold(ItemIds.AxeIron);
            Set(t, B.FlowerRed);
            yield return Frames(2);
            int d0 = P.Inventory.Slots[0].Durability;
            yield return MineAt(t, 2f);
            Check("plant_does_not_wear_tool", mMineBroke && P.Inventory.Slots[0].Durability == d0, $"dur {d0}->{P.Inventory.Slots[0].Durability}");

            // Last use breaks the tool and raises ToolBroke.
            yield return ClearDrops();
            Hold(ItemIds.ShovelWood, 1, 1);
            int broke = 0;
            Action<ItemDef> onBroke = _ => broke++;
            BI.ToolBroke += onBroke;
            Set(t, B.Dirt);
            yield return Frames(2);
            yield return MineAt(t, 3f);
            yield return Frames(2);
            BI.ToolBroke -= onBroke;
            Check("tool_breaks_at_zero", mMineBroke && P.Inventory.Slots[0].Id != ItemIds.ShovelWood && broke == 1, $"slot0={ItemDb.NameOf(P.Inventory.Slots[0].Id)} events={broke}");
            Set(t, B.Air); Set(support, B.Air);
            yield return ClearDrops();
        }

        // ------------------------------------------------------------------ placement

        IEnumerator Placement()
        {
            var inv = P.Inventory;
            Stand(mA);
            yield return Frames(3);

            // 1. Into water: the ray passes through water and the block replaces it.
            var wcell = new Vector3Int(mA.x + 3, Y0 + 1, mA.z + 2);
            Set(wcell, B.Water);
            Hold(B.Dirt);
            yield return Aim(new Vector3(wcell.x + 0.5f, Y0 + 1.0f, wcell.z + 0.5f));
            yield return Use();
            Check("place_into_water", Get(wcell) == B.Dirt && inv.Count(B.Dirt) == 63, $"cell={Blocks.Get(Get(wcell)).Name}");

            // 1b. A torch must not replace a water cell (it would punch a dry hole in a lake).
            Set(wcell, B.Water);
            Hold(B.Torch);
            yield return Aim(new Vector3(wcell.x + 0.5f, Y0 + 1.0f, wcell.z + 0.5f));
            yield return Use();
            Check("torch_not_placed_in_water", Get(wcell) == B.Water, $"cell={Blocks.Get(Get(wcell)).Name}");
            Set(wcell, B.Air);

            // 2. Onto replaceable plants.
            var g = new Vector3Int(mA.x - 2, Y0 + 1, mA.z + 2);
            Set(g, B.TallGrass);
            Hold(B.Cobblestone);
            yield return Aim(Center(g));
            yield return Use();
            Check("place_replaces_tall_grass", Get(g) == B.Cobblestone, $"cell={Blocks.Get(Get(g)).Name}");
            Set(g, B.Grass);
            Set(g + Vector3Int.up, B.Fern);
            Hold(B.FlowerBlue);
            yield return Aim(Center(g + Vector3Int.up));
            yield return Use();
            Check("flower_replaces_fern_on_grass", Get(g + Vector3Int.up) == B.FlowerBlue, $"cell={Blocks.Get(Get(g + Vector3Int.up)).Name}");
            Set(g + Vector3Int.up, B.Air); Set(g, B.Air);

            // 3. Support rules.
            var floorCell = new Vector3Int(mA.x, Y0 + 1, mA.z + 2);
            Hold(B.FlowerRed);
            yield return Aim(new Vector3(floorCell.x + 0.5f, Y0 + 1.0f, floorCell.z + 0.5f));
            yield return Use();
            Check("flower_rejected_on_stone", Get(floorCell) == B.Air, $"cell={Blocks.Get(Get(floorCell)).Name}");
            Hold(B.Torch);
            yield return Use();
            Check("torch_on_floor", Get(floorCell) == B.Torch, $"cell={Blocks.Get(Get(floorCell)).Name}");
            Set(floorCell, B.Air);
            // Torch against the side of a floating block: nothing below the target cell -> rejected.
            var wall = new Vector3Int(mA.x + 2, Y0 + 3, mA.z + 2);
            Set(wall, B.Planks);
            yield return Aim(new Vector3(wall.x + 0.02f, wall.y + 0.5f, wall.z + 0.5f));
            var side = BI.Target;
            yield return Use();
            Check("torch_needs_floor", side.Hit && Get(side.Adjacent) == B.Air, $"hit={side.Hit} adj={side.Adjacent} cell={Blocks.Get(Get(side.Adjacent)).Name}");
            Set(wall, B.Air);
            // Torch on leaves rejected.
            var leaf = new Vector3Int(mA.x - 2, Y0 + 1, mA.z + 2);
            Set(leaf, B.LeavesOak);
            yield return Aim(new Vector3(leaf.x + 0.5f, leaf.y + 1f, leaf.z + 0.5f));
            yield return Use();
            Check("torch_rejected_on_leaves", Get(leaf + Vector3Int.up) == B.Air, $"cell={Blocks.Get(Get(leaf + Vector3Int.up)).Name}");
            Set(leaf, B.Air);

            // 4. Torches and plants pop when their support is mined, and should drop as items.
            yield return ClearDrops();
            var sup = new Vector3Int(mA.x, Y0 + 1, mA.z + 2);
            Set(sup, B.Dirt);
            Set(sup + Vector3Int.up, B.Torch);
            Hold(ItemIds.ShovelIron);
            yield return MineAt(sup, 3f);
            yield return Frames(2);
            bool popped = Get(sup + Vector3Int.up) == B.Air;
            yield return new WaitForSeconds(1.8f);
            int torchDrops = ItemDrop.All.Count(d => d != null && d.Stack.Id == B.Torch);
            Check("torch_pops_without_support", popped, $"above={Blocks.Get(Get(sup + Vector3Int.up)).Name}");
            Check("popped_torch_drops_item", inv.Count(B.Torch) + torchDrops >= 1, $"torch in inv={inv.Count(B.Torch)} drops={torchDrops} dirt={inv.Count(B.Dirt)}");
            yield return ClearDrops();
            Set(sup, B.Grass);
            Set(sup + Vector3Int.up, B.FlowerYellow);
            Hold(ItemIds.ShovelIron);
            yield return MineAt(sup, 3f);
            yield return new WaitForSeconds(1.8f);
            Check("popped_flower_drops_item", Get(sup + Vector3Int.up) == B.Air && inv.Count(B.FlowerYellow) + ItemDrop.All.Count(d => d != null && d.Stack.Id == B.FlowerYellow) >= 1,
                $"above={Blocks.Get(Get(sup + Vector3Int.up)).Name} flowers={inv.Count(B.FlowerYellow)}");
            yield return ClearDrops();

            // 5. Furnace faces the player for each of the four look directions.
            var dirs = new[] { (0f, new Vector3Int(0, 0, 2), (byte)2), (90f, new Vector3Int(2, 0, 0), (byte)3), (180f, new Vector3Int(0, 0, -2), (byte)0), (270f, new Vector3Int(-2, 0, 0), (byte)1) };
            var faced = new List<string>();
            bool facingOk = true;
            foreach (var (yaw, off, expectMeta) in dirs)
            {
                Hold(B.Furnace);
                var cell = new Vector3Int(mA.x, Y0 + 1, mA.z) + off;
                yield return Aim(new Vector3(cell.x + 0.5f, Y0 + 1.0f, cell.z + 0.5f));
                yield return Use();
                byte meta = W.GetMeta(cell.x, cell.y, cell.z);
                faced.Add($"{yaw:0}:{(Get(cell) == B.Furnace ? meta.ToString() : "none")}");
                facingOk &= Get(cell) == B.Furnace && meta == expectMeta;
            }
            Check("furnace_faces_player", facingOk, string.Join(" ", faced) + " (expect 0:2 90:3 180:0 270:1)");
            mFurnaces = dirs.Select(d => new Vector3Int(mA.x, Y0 + 1, mA.z) + d.Item2).ToArray();

            // 6. Never inside the player: straight down (feet cell) and straight up under a ceiling (head cell).
            Hold(B.Dirt);
            P.Pitch = 89.9f; P.Yaw = 45f;
            yield return Frames(3);
            int before = inv.Count(B.Dirt);
            yield return Use();
            Check("no_place_in_feet", inv.Count(B.Dirt) == before, $"dirt {before}->{inv.Count(B.Dirt)}");
            var ceil = new Vector3Int(mA.x, Y0 + 3, mA.z);
            Set(ceil, B.Stone);
            P.Pitch = -89.9f;
            yield return Frames(3);
            before = inv.Count(B.Dirt);
            yield return Use();
            Check("no_place_in_head", inv.Count(B.Dirt) == before && Get(ceil + Vector3Int.down) == B.Air, $"dirt {before}->{inv.Count(B.Dirt)}");
            P.ScriptedCrouch = true;
            yield return new WaitForSeconds(0.3f);
            yield return Use();
            Check("no_place_in_head_crouched", Get(ceil + Vector3Int.down) == B.Air, $"cell={Blocks.Get(Get(ceil + Vector3Int.down)).Name}");
            P.ScriptedCrouch = false;
            Set(ceil, B.Air);
            yield return Frames(3);

            // 7. Never inside wildlife.
            var wm = Wildlife.WildlifeManager.Instance;
            if (wm != null)
            {
                var deerCell = new Vector3Int(mA.x - 3, Y0 + 1, mA.z + 1);
                var deer = wm.SpawnAt(Wildlife.Species.Deer, new Vector3(deerCell.x + 0.5f, Y0 + 1f, deerCell.z + 0.5f));
                Hold(B.Dirt);
                yield return Aim(new Vector3(deerCell.x + 0.5f, Y0 + 1.0f, deerCell.z + 0.5f));
                var tgt = BI.Target;
                yield return Use();
                Check("no_place_in_wildlife", deer != null && Get(deerCell) == B.Air && Get(deerCell + Vector3Int.up) == B.Air,
                    $"deer={(deer != null)} target={tgt.Block} adj={tgt.Adjacent} cell={Blocks.Get(Get(deerCell)).Name}");
                wm.Clear();
                yield return null;
            }
            else Check("no_place_in_wildlife", false, "no WildlifeManager");

            // 8. Reach: 4.5 m reachable, 5.5 m not.
            var near = new Vector3Int(mA.x, Y0 + 2, mA.z + 5);
            var far = new Vector3Int(mA.x - 1, Y0 + 2, mA.z + 6);
            Set(near, B.Planks); Set(far, B.Planks);
            yield return Aim(Center(near));
            bool nearHit = BI.Target.Hit && BI.Target.Block == near;
            yield return Aim(Center(far));
            bool farHit = BI.Target.Hit;
            Hold(ItemIds.AxeIron);
            BI.ScriptedAttack = true;
            yield return new WaitForSeconds(1.2f);
            BI.ScriptedAttack = false;
            Hold(B.Dirt);
            yield return Use();
            Check("reach_limit", nearHit && !farHit && Get(far) == B.Planks && P.Inventory.Count(B.Dirt) == 64,
                $"nearHit={nearHit} farHit={farHit} eyeToFar={(Center(far) - P.EyePosition).magnitude:F2}m");
            Set(near, B.Air); Set(far, B.Air);
            yield return ClearDrops();
        }

        Vector3Int[] mFurnaces = new Vector3Int[0];

        // ------------------------------------------------------------------ crafting

        IEnumerator CraftingChecks()
        {
            var C = new Vector3Int(mA.x - 8, Y0, mA.z + 8); // > 5 blocks from the furnaces left by the facing test
            Stand(C);
            yield return Frames(3);
            var inv = P.Inventory;
            var wb = new Vector3Int(C.x + 3, Y0 + 1, C.z - 3);
            var fu = new Vector3Int(C.x - 1, Y0 + 1, C.z - 3);
            Set(wb, B.Workbench); Set(fu, B.Furnace);
            var near = Crafting.StationsNear(W, P.transform.position);
            Check("stations_detected", near.Contains(Station.Workbench) && near.Contains(Station.Furnace), string.Join(",", near));

            int ok = 0;
            var failed = new List<string>();
            foreach (var r in Crafting.Recipes)
            {
                inv.Clear();
                foreach (var (id, count) in r.Inputs) inv.Add(id, count);
                bool c = Crafting.Craft(inv, r, Crafting.StationsNear(W, P.transform.position));
                bool exact = c && inv.Count(r.Output) == r.OutputCount && r.Inputs.All(i => inv.Count(i.id) == 0);
                if (exact) ok++; else failed.Add(r.Key);
            }
            Check("every_recipe_crafts_in_world", failed.Count == 0, $"ok={ok}/{Crafting.Recipes.Count} failed={string.Join(",", failed)}");

            Set(wb, B.Air); Set(fu, B.Air);
            near = Crafting.StationsNear(W, P.transform.position);
            int gated = 0;
            foreach (var r in Crafting.Recipes.Where(r => r.Station != Station.None))
            {
                inv.Clear();
                foreach (var (id, count) in r.Inputs) inv.Add(id, count);
                if (!Crafting.Craft(inv, r, near) && r.Inputs.All(i => inv.Count(i.id) == i.count)) gated++;
            }
            int stationRecipes = Crafting.Recipes.Count(r => r.Station != Station.None);
            Check("station_gating_in_world", near.Count == 0 && gated == stationRecipes, $"gated={gated}/{stationRecipes}");

            // Range: 4 blocks away counts, 6 does not; a diagonal 7 m away should not either (StationRange = 4.5).
            Set(new Vector3Int(C.x + 4, Y0 + 1, C.z), B.Workbench);
            bool at4 = Crafting.StationsNear(W, P.transform.position).Contains(Station.Workbench);
            Set(new Vector3Int(C.x + 4, Y0 + 1, C.z), B.Air);
            Set(new Vector3Int(C.x + 6, Y0 + 1, C.z), B.Workbench);
            bool at6 = Crafting.StationsNear(W, P.transform.position).Contains(Station.Workbench);
            Set(new Vector3Int(C.x + 6, Y0 + 1, C.z), B.Air);
            var diag = new Vector3Int(C.x + 5, Y0 + 1, C.z - 5);
            Set(diag, B.Workbench);
            bool atDiag = Crafting.StationsNear(W, P.transform.position).Contains(Station.Workbench);
            float diagDist = Vector3.Distance(P.transform.position + Vector3.up * 0.9f, Center(diag));
            Set(diag, B.Air);
            Check("station_range_4_yes_6_no", at4 && !at6, $"at4={at4} at6={at6}");
            Check("station_range_is_radius", !atDiag, $"workbench {diagDist:F1} m away diagonally counts as near={atDiag} (StationRange={Crafting.StationRange})");

            // Right-click opens crafting; the UI captures the mouse and Back() releases it.
            Set(wb, B.Workbench);
            int used = 0;
            Action<Station> onUse = s => used++;
            BI.StationUsed += onUse;
            Hold(B.Dirt);
            yield return Aim(Center(wb));
            yield return Use();
            yield return Frames(3);
            bool captured = R.UiCapture;
            var ui = UI.UiRoot.Instance;
            if (ui != null) ui.Back();
            yield return Frames(5);
            BI.StationUsed -= onUse;
            Check("workbench_opens_crafting", used >= 1 && captured && !R.UiCapture, $"events={used} captured={captured} after={R.UiCapture}");
            Set(wb, B.Air);

            // Full inventory in the world: crafting fails and nothing moves.
            inv.Clear();
            inv.Slots[0] = ItemStack.Of(B.LogOak, 64);
            for (int i = 1; i < Inventory.Size; i++) inv.Slots[i] = ItemStack.Of(B.Gravel, 64);
            inv.Notify();
            var snap = (ItemStack[])inv.Slots.Clone();
            bool crafted = Crafting.Craft(inv, Crafting.Recipes.First(r => r.Key == "planks_" + B.LogOak), near);
            bool same = true;
            for (int i = 0; i < Inventory.Size; i++) same &= snap[i].Id == inv.Slots[i].Id && snap[i].Count == inv.Slots[i].Count;
            Check("full_inventory_rollback_in_world", !crafted && same);
            inv.Clear();
        }

        // ------------------------------------------------------------------ drops

        IEnumerator Drops()
        {
            var inv = P.Inventory;
            yield return ClearDrops();
            Stand(mA);
            inv.Clear();
            var L = new Vector3(mA.x + 6.5f, Y0 + 1.3f, mA.z - 6.5f); // > 2.4 m from the player: not attracted
            ItemDrop.Spawn(ItemStack.Of(B.Dirt, 1), L, Vector3.zero, 0.6f);
            ItemDrop.Spawn(ItemStack.Of(B.Dirt, 3), L + new Vector3(0.3f, 0, 0.2f), Vector3.zero, 0.6f);
            ItemDrop.Spawn(ItemStack.Of(ItemIds.PickaxeWood, 1), L + new Vector3(-0.2f, 0, 0), Vector3.zero, 0.6f);
            ItemDrop.Spawn(ItemStack.Of(ItemIds.PickaxeWood, 1), L + new Vector3(-0.3f, 0, 0.1f), Vector3.zero, 0.6f);
            yield return new WaitForSeconds(1.5f);
            var dirt = ItemDrop.All.Where(d => d != null && d.Stack.Id == B.Dirt).ToList();
            var picks = ItemDrop.All.Where(d => d != null && d.Stack.Id == ItemIds.PickaxeWood).ToList();
            Check("drops_merge", dirt.Count == 1 && dirt[0].Stack.Count == 4, $"dirtDrops={dirt.Count} counts={string.Join(",", dirt.Select(d => d.Stack.Count))}");
            Check("tool_drops_do_not_merge", picks.Count == 2, $"pickDrops={picks.Count}");
            Check("drops_rest_on_floor", dirt.Count == 1 && Mathf.Abs(dirt[0].transform.position.y - (Y0 + 1.12f)) < 0.1f, dirt.Count == 1 ? $"y={dirt[0].transform.position.y:F2}" : "");

            // Pickup: walk onto them.
            Stand(new Vector3Int(mA.x + 6, Y0, mA.z - 7));
            yield return new WaitForSeconds(1.5f);
            Check("drops_picked_up", inv.Count(B.Dirt) == 4 && inv.Count(ItemIds.PickaxeWood) == 2 && ItemDrop.All.Count(d => d != null) == 0,
                $"dirt={inv.Count(B.Dirt)} picks={inv.Count(ItemIds.PickaxeWood)} left={ItemDrop.All.Count(d => d != null)}");

            // Full inventory: the drop stays and the inventory does not change.
            for (int i = 0; i < Inventory.Size; i++) inv.Slots[i] = ItemStack.Of(B.Stone, 64);
            inv.Notify();
            int changed = 0;
            Action onChanged = () => changed++;
            inv.Changed += onChanged;
            var full = ItemDrop.Spawn(ItemStack.Of(B.Dirt, 2), P.transform.position + Vector3.up * 0.5f, Vector3.zero, 0.2f);
            yield return new WaitForSeconds(2f);
            int frames0 = Time.frameCount;
            changed = 0;
            float tw = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(1f);
            int frames = Time.frameCount - frames0;
            float secs = Time.realtimeSinceStartup - tw;
            inv.Changed -= onChanged;
            Check("full_inventory_leaves_drop", full != null && full.Stack.Count == 2 && inv.Count(B.Dirt) == 0 && inv.Count(B.Stone) == 64 * 36,
                $"drop={(full != null ? full.Stack.Count : -1)} dirt={inv.Count(B.Dirt)}");
            // Frame cost with and without the blocked drop (uncapped in batch mode, so this is CPU time).
            float tA = Time.realtimeSinceStartup; int fA = Time.frameCount;
            yield return new WaitForSecondsRealtime(2f);
            float msWith = 1000f * (Time.realtimeSinceStartup - tA) / Mathf.Max(1, Time.frameCount - fA);
            var fullPos = full != null ? full.transform.position : Vector3.zero;
            if (full != null) Destroy(full.gameObject);
            yield return null;
            tA = Time.realtimeSinceStartup; fA = Time.frameCount;
            yield return new WaitForSecondsRealtime(2f);
            float msWithout = 1000f * (Time.realtimeSinceStartup - tA) / Mathf.Max(1, Time.frameCount - fA);
            full = ItemDrop.Spawn(ItemStack.Of(B.Dirt, 2), fullPos, Vector3.zero, 0.2f);
            yield return new WaitForSeconds(0.5f);
            Check("no_inventory_event_spam_on_blocked_pickup", changed <= 2,
                $"Inventory.Changed fired {changed} times in {secs:F1}s ({frames} frames) while standing on a drop with a full inventory; frame {msWith:F2} ms vs {msWithout:F2} ms without the drop");

            // Partial pickup into the last free room.
            inv.Slots[7].Count = 63;
            inv.Notify();
            var partial = ItemDrop.Spawn(ItemStack.Of(B.Stone, 5), P.transform.position + Vector3.up * 0.5f, Vector3.zero, 0.2f);
            yield return new WaitForSeconds(1f);
            Check("partial_pickup", partial != null && partial.Stack.Count == 4 && inv.Slots[7].Count == 64, $"left={(partial != null ? partial.Stack.Count : -1)}");
            yield return ClearDrops();

            // Lifetime: drops vanish after 300 s.
            var old = ItemDrop.Spawn(ItemStack.Of(B.Sand, 1), L, Vector3.zero);
            old.Age = 299.95f;
            yield return new WaitForSeconds(0.3f);
            Check("drop_despawns_after_lifetime", old == null);
            inv.Clear();
        }

        // ------------------------------------------------------------------ movement

        IEnumerator Movement()
        {
            // Crouch never walks off the platform edge (four headings incl. diagonal into a corner).
            var edgeResults = new List<string>();
            bool edgeOk = true;
            foreach (var (cell, yaw) in new[] { (new Vector3Int(mA.x + 9, Y0, mA.z), 90f), (new Vector3Int(mA.x - 9, Y0, mA.z), 270f),
                                                 (new Vector3Int(mA.x, Y0, mA.z + 9), 0f), (new Vector3Int(mA.x + 9, Y0, mA.z + 9), 45f) })
            {
                Stand(cell);
                yield return new WaitForSeconds(0.3f);
                P.Yaw = yaw;
                P.ScriptedCrouch = true;
                P.ScriptedMove = new Vector2(0, 1);
                P.ScriptedSprint = true;
                yield return new WaitForSeconds(2.5f);
                P.ScriptedMove = new Vector2(1, 1);
                yield return new WaitForSeconds(1f);
                var pos = P.transform.position;
                bool on = pos.y > Y0 + 0.9f && P.Grounded;
                edgeOk &= on;
                edgeResults.Add($"{yaw:0}:{(on ? "held" : $"fell y={pos.y:F1}")}@{pos.x - mA.x:F2},{pos.z - mA.z:F2}");
                P.ScriptedMove = Vector2.zero; P.ScriptedCrouch = false; P.ScriptedSprint = false;
                yield return new WaitForSeconds(0.2f);
            }
            Check("crouch_never_walks_off_ledge", edgeOk, string.Join(" ", edgeResults));

            // Swim up out of a 3-deep pool whose surface is one block below the platform.
            yield return BuildPool();
            var bottom = new Vector3(mPool.x + 0.5f, mPool.y + 0.001f, mPool.z + 0.5f);
            P.Flying = false;
            P.Teleport(bottom);
            yield return new WaitForSeconds(0.6f);
            bool wasUnder = P.HeadUnderwater;
            P.Yaw = 270f; // toward -X, the pool wall nearest the platform centre
            P.ScriptedJump = true;
            P.ScriptedMove = new Vector2(0, 1);
            float t0 = Time.time;
            while (Time.time - t0 < 10f && !(P.Grounded && P.transform.position.y > Y0 + 0.9f)) yield return null;
            float took = Time.time - t0;
            P.ScriptedJump = false; P.ScriptedMove = Vector2.zero;
            yield return new WaitForSeconds(1.5f); // a buffered jump may still fire on landing
            Check("swim_up_and_out", wasUnder && P.transform.position.y > Y0 + 0.9f && !P.HeadUnderwater && P.Grounded,
                $"startUnder={wasUnder} took={took:F1}s y={P.transform.position.y - Y0:F2} (platform top = +1)");

            // Plain jump height ~1.3 blocks; walking speed sane.
            Stand(mA);
            yield return new WaitForSeconds(0.4f);
            float baseY = P.transform.position.y, peak = baseY;
            P.ScriptedJump = true;
            for (float t = 0; t < 0.7f; t += Time.deltaTime) { peak = Mathf.Max(peak, P.transform.position.y); yield return null; }
            P.ScriptedJump = false;
            yield return new WaitForSeconds(0.6f);
            Check("jump_height", peak - baseY > 1.1f && peak - baseY < 1.6f, $"h={peak - baseY:F2}");
            P.Yaw = 45f;
            var s0 = P.transform.position;
            P.ScriptedMove = new Vector2(0, 1);
            yield return new WaitForSeconds(1f);
            var s1 = P.transform.position;
            yield return new WaitForSeconds(0.5f);
            var s2 = P.transform.position;
            P.ScriptedMove = Vector2.zero;
            float speed = (s2 - s1).magnitude / 0.5f;
            Check("walk_speed", Mathf.Abs(speed - PlayerController.WalkSpeed) < 0.5f, $"v={speed:F2} m/s");
            _ = s0;
        }

        Vector3Int mPool;

        IEnumerator BuildPool()
        {
            // Shell x[+3..+7] z[-7..-3] y[Y0-4..Y0]; water x[+4..+6] z[-6..-4] y[Y0-3..Y0-1]; air at Y0 inside.
            int x0 = mA.x + 3, z0 = mA.z - 7;
            Fill(x0, Y0 - 4, z0, x0 + 4, Y0, z0 + 4, B.Stone);
            Fill(x0 + 1, Y0 - 3, z0 + 1, x0 + 3, Y0 - 1, z0 + 3, B.Water);
            Fill(x0 + 1, Y0, z0 + 1, x0 + 3, Y0, z0 + 3, B.Air);
            mPool = new Vector3Int(x0 + 2, Y0 - 3, z0 + 2);
            yield return Frames(3);
        }

        // ------------------------------------------------------------------ damage / death

        float mLastDamage; string mLastCause;
        void OnDamaged(float a, string c) { mLastDamage += a; mLastCause = c; }

        IEnumerator FallFrom(float h, float hp = PlayerController.MaxHealth)
        {
            Stand(mA);
            yield return new WaitForSeconds(0.3f);
            P.Health = hp;
            mLastDamage = 0; mLastCause = null;
            P.Teleport(new Vector3(mA.x + 0.5f, Y0 + 1.001f + h, mA.z + 0.5f));
            yield return null;
            float t0 = Time.time;
            while (Time.time - t0 < 5f && !(P.Grounded && P.transform.position.y < Y0 + 1.2f)) yield return null;
            yield return new WaitForSeconds(0.2f);
        }

        IEnumerator Damage()
        {
            P.Damaged += OnDamaged;
            var res = new List<string>();
            bool fallOk = true;
            foreach (var h in new[] { 3f, 3.5f, 4f, 5f, 6f, 10f })
            {
                for (int trial = 0; trial < (h == 4f || h == 5f ? 3 : 1); trial++)
                {
                    yield return FallFrom(h);
                    float expect = h >= 4f ? Mathf.Floor(h - 3f) : 0f;
                    res.Add($"{h}m:{mLastDamage:0}");
                    fallOk &= Mathf.Approximately(mLastDamage, expect) && (expect == 0 || mLastCause == "fall");
                }
            }
            Check("fall_damage_thresholds", fallOk, string.Join(" ", res) + " (expect floor(h-3) for h>=4)");

            // Landing in water: no damage.
            yield return BuildPool();
            Stand(mA);
            yield return new WaitForSeconds(0.3f);
            P.Health = PlayerController.MaxHealth; mLastDamage = 0;
            P.Teleport(new Vector3(mPool.x + 0.5f, Y0 + 12f, mPool.z + 0.5f));
            yield return new WaitForSeconds(2.5f);
            Check("water_landing_no_damage", mLastDamage == 0f && P.Submerged > 0.3f, $"dmg={mLastDamage} submerged={P.Submerged:F2}");

            // Drowning: air runs out, then 2 damage per second.
            P.Teleport(new Vector3(mPool.x + 0.5f, mPool.y + 0.001f, mPool.z + 0.5f));
            P.Health = PlayerController.MaxHealth; P.Air = 1.5f; mLastDamage = 0; mLastCause = null;
            yield return new WaitForSeconds(4.2f);
            float hp = P.Health, air = P.Air;
            Check("drowning", air == 0f && mLastCause == "drown" && hp <= 16.01f && hp >= 13.99f, $"air={air:F2} hp={hp:F1} cause={mLastCause}");
            Stand(mA);
            yield return new WaitForSeconds(1.5f);
            Check("air_refills", P.Air > 4f, $"air={P.Air:F2}");

            // Death and respawn through the UI.
            int died = 0;
            Action onDied = () => died++;
            P.Died += onDied;
            yield return FallFrom(6f, 1f);
            yield return Frames(5);
            var ui = UI.UiRoot.Instance;
            bool dead = P.Dead, deathScreen = ui != null && ui.Top == ui.Death, captured = R.UiCapture;
            // Attacking while dead does nothing.
            Hold(ItemIds.PickaxeIron);
            BI.ScriptedAttack = true;
            yield return Frames(10);
            bool noTarget = !BI.Target.Hit;
            BI.ScriptedAttack = false;
            Check("death", dead && died == 1 && deathScreen && captured && noTarget, $"dead={dead} events={died} deathScreen={deathScreen} capture={captured}");
            if (ui != null && ui.Death != null) ui.Death.Respawn(); else P.Respawn();
            yield return Frames(5);
            var p = P.transform.position;
            int sy = W.SurfaceY(mSpawnCol.x, mSpawnCol.z);
            Check("respawn_restores", !P.Dead && P.Health == PlayerController.MaxHealth && P.Air == PlayerController.MaxAir && P.InputEnabled && !R.UiCapture,
                $"hp={P.Health} air={P.Air} input={P.InputEnabled} capture={R.UiCapture}");
            Check("respawn_on_surface_at_spawn", Mathf.FloorToInt(p.x) == mSpawnCol.x && Mathf.FloorToInt(p.z) == mSpawnCol.z && Mathf.Abs(p.y - (sy + 1.01f)) < 0.05f,
                $"pos={p} surface={sy}");
            yield return new WaitForSeconds(1.5f);
            Check("respawn_lands_safely", P.Health == PlayerController.MaxHealth && P.Grounded && !VoxelPhysics.Overlaps(W, P.transform.position + new Vector3(-0.3f, 0.01f, -0.3f), P.transform.position + new Vector3(0.3f, 1.8f, 0.3f)),
                $"hp={P.Health} grounded={P.Grounded}");
            P.Died -= onDied;
            P.Damaged -= OnDamaged;
        }

        // ------------------------------------------------------------------ pause / resume

        IEnumerator PauseResume()
        {
            Stand(mA);
            yield return new WaitForSeconds(0.4f);
            var atm = Rendering.Atmosphere.Instance;
            float tod0 = atm != null ? atm.TimeOfDay : 0;
            var pos0 = P.transform.position;
            R.Pause();
            yield return null;
            bool paused = R.State == GameState.Paused && Time.timeScale == 0f;
            P.ScriptedMove = new Vector2(0, 1);
            yield return new WaitForSecondsRealtime(1.2f);
            bool frozen = (P.transform.position - pos0).magnitude < 0.001f && (atm == null || Mathf.Approximately(atm.TimeOfDay, tod0));
            R.Resume();
            yield return new WaitForSeconds(0.6f);
            bool resumed = R.State == GameState.Playing && Time.timeScale == 1f && (P.transform.position - pos0).magnitude > 0.5f;
            P.ScriptedMove = Vector2.zero;
            Check("pause_freezes_world", paused && frozen, $"state={R.State} moved={(P.transform.position - pos0).magnitude:F2}");
            Check("resume", resumed, $"timeScale={Time.timeScale}");
            var ui = UI.UiRoot.Instance;
            Check("pause_screen_closed_after_resume", ui == null || ui.Top == null, $"top={ui?.Top?.GetType().Name}");
        }

        // ------------------------------------------------------------------ settings

        IEnumerator SettingsChecks()
        {
            // Command-line overrides (-preset/-viewdist/-novsync) are session-only; saving settings must not persist them.
            if (Cli.Has("preset") || Cli.Has("viewdist") || Cli.Has("novsync"))
            {
                var fileBefore = mSettingsBackup != null ? JsonUtility.FromJson<GameSettings>(System.Text.Encoding.UTF8.GetString(mSettingsBackup)) : new GameSettings();
                GameSettings.Save(); // what the settings screen's Apply / the UI harness restore do
                var fileAfter = JsonUtility.FromJson<GameSettings>(File.ReadAllText(mSettingsPath));
                Check("cli_overrides_not_persisted", fileAfter.Preset == fileBefore.Preset && fileAfter.ViewDistance == fileBefore.ViewDistance && fileAfter.VSync == fileBefore.VSync,
                    $"file before: preset={fileBefore.Preset} view={fileBefore.ViewDistance} vsync={fileBefore.VSync} -> after Save(): preset={fileAfter.Preset} view={fileAfter.ViewDistance} vsync={fileAfter.VSync}");
                RestoreSettings();
                mSettingsRestored = false;
            }
            var s = GameSettings.Current;
            var orig = JsonUtility.ToJson(s);
            s.ViewDistance = 7; s.Fov = 88f; s.MouseSensitivity = 1.7f; s.InvertY = !s.InvertY; s.MasterVolume = 0.33f;
            s.Preset = GraphicsPreset.Custom; s.Bind(Control.Jump, KeyCode.F); s.HeadBob = !s.HeadBob; s.Brightness = -0.4f;
            var expect = JsonUtility.ToJson(s);
            GameSettings.Save();
            GameSettings.Load();
            var back = JsonUtility.ToJson(GameSettings.Current);
            Check("settings_persist", back == expect && GameSettings.Current.Key(Control.Jump) == KeyCode.F, back == expect ? "" : $"saved={expect.Length}B loaded={back.Length}B");
            GameSettings.Current.ResetBindings();
            Check("bindings_reset", GameSettings.Current.Key(Control.Jump) == KeyCode.Space);

            // Corrupt file -> defaults, no exception.
            File.WriteAllText(mSettingsPath, "{ not json");
            GameSettings.Load();
            Check("settings_corrupt_falls_back", GameSettings.Current != null && GameSettings.Current.ViewDistance == new GameSettings().ViewDistance);

            // Runtime preset switching.
            JsonUtility.FromJsonOverwrite(orig, GameSettings.Current);
            Stand(mA);
            var res = new List<string>();
            bool presetsOk = true;
            foreach (var preset in new[] { GraphicsPreset.Low, GraphicsPreset.Ultra, GraphicsPreset.Medium, GraphicsPreset.High })
            {
                int errs = mErrors.Count;
                GameSettings.Current.ApplyPreset(preset);
                GameSettings.NotifyChanged();
                float t0 = Time.realtimeSinceStartup;
                yield return Frames(3);
                yield return Settle(60f);
                var miss = UnmeshedInView(out int total);
                bool ok = W.ViewDistance == GameSettings.Current.ViewDistance && mErrors.Count == errs && W.FancyLeaves == GameSettings.Current.FancyLeaves;
                presetsOk &= ok;
                res.Add($"{preset}:view={W.ViewDistance} settle={Time.realtimeSinceStartup - t0:F1}s unmeshed={miss.Count}/{total} errors={mErrors.Count - errs}");
            }
            Check("preset_switching_runtime", presetsOk, string.Join(" | ", res));
            RestoreSettings();
            GameSettings.Load();
            GameSettings.NotifyChanged();
            mSettingsRestored = false; // restore again at exit too
            yield return Settle(30f);
        }

        void RestoreSettings()
        {
            if (mSettingsRestored || mSettingsPath == null) return;
            mSettingsRestored = true;
            try
            {
                if (mSettingsBackup != null) File.WriteAllBytes(mSettingsPath, mSettingsBackup);
                else if (File.Exists(mSettingsPath)) File.Delete(mSettingsPath);
                if (File.Exists(mSettingsPath + ".qa-backup")) File.Delete(mSettingsPath + ".qa-backup");
            }
            catch (Exception e) { Debug.LogWarning("WM_QA_INFO settings restore failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ streaming

        IEnumerator Streaming()
        {
            var p = P;
            p.Flying = true;
            var start = p.transform.position + new Vector3(0, 30, 0);
            p.Teleport(start);
            yield return Settle(30f);
            var m0 = UnmeshedInView(out int tot0);
            Check("all_in_view_meshed_at_rest", m0.Count == 0, $"view={W.ViewDistance} unmeshed={m0.Count}/{tot0} {Fmt(m0)}");

            // Fly flat-out (sprint flying, ~22 m/s) for 20 s, recording how often the player's own area is ready.
            p.Yaw = 30f; p.Pitch = 5f;
            p.ScriptedMove = new Vector2(0, 1);
            p.ScriptedSprint = true;
            int frames = 0, ready1 = 0, ready2 = 0;
            float worst = 0, t0 = Time.time;
            var from = p.transform.position;
            while (Time.time - t0 < 20f)
            {
                frames++;
                if (W.ReadyAround(p.transform.position, 1)) ready1++;
                if (W.ReadyAround(p.transform.position, 2)) ready2++;
                worst = Mathf.Max(worst, Time.unscaledDeltaTime);
                yield return null;
            }
            p.ScriptedMove = Vector2.zero; p.ScriptedSprint = false;
            float dist = (p.transform.position - from).magnitude;
            Check("fly_fast_streams_ahead", ready1 >= frames * 0.98f,
                $"dist={dist:F0}m speed={dist / 20f:F1}m/s ready(r1)={100f * ready1 / Mathf.Max(1, frames):F1}% ready(r2)={100f * ready2 / Mathf.Max(1, frames):F1}% worstFrame={worst * 1000f:F0}ms");
            float ts = Time.realtimeSinceStartup;
            yield return Settle(45f);
            var m1 = UnmeshedInView(out int tot1);
            Check("no_holes_after_flight", m1.Count == 0, $"settle={Time.realtimeSinceStartup - ts:F1}s unmeshed={m1.Count}/{tot1} {Fmt(m1)}");

            // Teleport 2 km and settle.
            p.Teleport(p.transform.position + new Vector3(2000, 0, -1500));
            ts = Time.realtimeSinceStartup;
            yield return WaitArea(p.transform.position, 2, 60f);
            float near = Time.realtimeSinceStartup - ts;
            yield return Settle(60f);
            var m2 = UnmeshedInView(out int tot2);
            Check("no_holes_after_teleport", m2.Count == 0, $"nearReady={near:F1}s settle={Time.realtimeSinceStartup - ts:F1}s unmeshed={m2.Count}/{tot2} {Fmt(m2)} loaded={W.Loaded}");
            // Memory of a full view ring: loaded column count stays bounded by the unload radius.
            int vd = W.ViewDistance, maxCols = (int)(Mathf.PI * (vd + 3.5f) * (vd + 3.5f)) + 64;
            Check("loaded_columns_bounded", W.Loaded <= maxCols, $"loaded={W.Loaded} bound~{maxCols}");

            // Back to the arena.
            p.Teleport(new Vector3(mA.x + 0.5f, Y0 + 1.001f, mA.z + 0.5f));
            p.Flying = false;
            yield return WaitArea(p.transform.position, 2, 60f);
            yield return Settle(30f);
            Check("arena_survived_round_trip", Get(mA) == B.Stone && (mFurnaces.Length == 0 || Get(mFurnaces[0]) == B.Furnace), "edits re-applied after unload");
        }

        // ------------------------------------------------------------------ wildlife / world content

        IEnumerator WildlifeAndWorld()
        {
            // Let wildlife spawn around the player on real terrain.
            int sy = W.SurfaceY(mSpawnCol.x, mSpawnCol.z);
            P.Flying = false;
            P.Teleport(new Vector3(mSpawnCol.x + 0.5f, sy + 1.01f, mSpawnCol.z + 0.5f));
            Wildlife.WildlifeManager.Enabled = true;
            yield return new WaitForSeconds(25f);
            var wm = Wildlife.WildlifeManager.Instance;
            int n = 0, stuck = 0, floating = 0, onLeaves = 0;
            var notes = new List<string>();
            if (wm != null)
                foreach (var a in wm.Animals)
                {
                    if (a == null || a.Kind == Wildlife.Species.Songbird) continue;
                    n++;
                    var pos = a.transform.position;
                    var min = new Vector3(pos.x - a.HalfWidth + 0.05f, pos.y + 0.05f, pos.z - a.HalfWidth + 0.05f);
                    var max = new Vector3(pos.x + a.HalfWidth - 0.05f, pos.y + a.Height, pos.z + a.HalfWidth - 0.05f);
                    byte below = W.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y - 0.05f), Mathf.FloorToInt(pos.z));
                    if (VoxelPhysics.OverlapsVoxels(W, min, max)) { stuck++; notes.Add($"{a.Kind}@{pos:F1} in block"); }
                    if (!Blocks.IsSolid[below]) { floating++; notes.Add($"{a.Kind}@{pos:F1} over {Blocks.Get(below).Name}"); }
                    if (Blocks.IsLeaves(below)) { onLeaves++; notes.Add($"{a.Kind}@{pos:F1} on leaves"); }
                }
            Check("wildlife_spawns", n > 0, $"walkers={n} total={wm?.Count}");
            Check("wildlife_not_in_blocks", stuck == 0, $"stuck={stuck}/{n} {string.Join("; ", notes.Take(6))}");
            Check("wildlife_on_ground", floating == 0 && onLeaves == 0, $"floating={floating} onLeaves={onLeaves}");

            // Floating trees: connected log groups (26-neighbourhood) where no log stands on solid, non-leaf ground.
            var logSet = new HashSet<Vector3Int>();
            int pcx = Mathf.FloorToInt(P.transform.position.x) >> 4, pcz = Mathf.FloorToInt(P.transform.position.z) >> 4;
            int R0 = 4;
            for (int cz = pcz - R0; cz <= pcz + R0; cz++)
                for (int cx = pcx - R0; cx <= pcx + R0; cx++)
                {
                    var c = W.GetChunk(cx, cz);
                    if (c == null || c.State != ChunkState.Generated) continue;
                    for (int y = 1; y <= c.MaxY; y++)
                        for (int z = 0; z < 16; z++)
                            for (int x = 0; x < 16; x++)
                                if (Blocks.IsLog(c.Blocks[Chunk.Index(x, y, z)])) logSet.Add(new Vector3Int(cx * 16 + x, y, cz * 16 + z));
                }
            int minX = (pcx - R0) * 16, maxX = (pcx + R0 + 1) * 16 - 1, minZ = (pcz - R0) * 16, maxZ = (pcz + R0 + 1) * 16 - 1;
            var seen = new HashSet<Vector3Int>();
            int groups = 0, floatingGroups = 0;
            var fl = new List<string>();
            foreach (var start in logSet)
            {
                if (!seen.Add(start)) continue;
                groups++;
                var q = new Queue<Vector3Int>(); q.Enqueue(start);
                bool grounded = false, edge = false; int size = 0; var low = start;
                while (q.Count > 0)
                {
                    var v = q.Dequeue(); size++;
                    if (v.y < low.y) low = v;
                    if (v.x <= minX || v.x >= maxX || v.z <= minZ || v.z >= maxZ) edge = true;
                    byte below = W.GetBlock(v.x, v.y - 1, v.z);
                    if (Blocks.IsSolid[below] && !Blocks.IsLeaves(below) && !Blocks.IsLog(below)) grounded = true;
                    for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                    {
                        var nb = new Vector3Int(v.x + dx, v.y + dy, v.z + dz);
                        if (logSet.Contains(nb) && seen.Add(nb)) q.Enqueue(nb);
                    }
                }
                if (grounded || edge) continue;
                floatingGroups++;
                if (fl.Count < 6) fl.Add($"{size} logs lowest ({low.x},{low.y},{low.z}) over {Blocks.Get(W.GetBlock(low.x, low.y - 1, low.z)).Name}");
            }
            Check("no_floating_trees", floatingGroups == 0, $"logs={logSet.Count} trees={groups} floating={floatingGroups} {string.Join("; ", fl)}");

            // Props removed when their support is mined.
            var pr = World.Decor.PropRenderer.Instance;
            var nearProp = pr?.Nearest(P.transform.position);
            if (nearProp.HasValue)
            {
                int before = pr.InstanceCount;
                var v = nearProp.Value;
                var cell = new Vector3Int(Mathf.FloorToInt(v.x), Mathf.FloorToInt(v.y), Mathf.FloorToInt(v.z));
                // Nearest() reports the model position; its support is the block under the anchor cell.
                int supY = cell.y - 1;
                W.SetBlock(cell.x, supY, cell.z, B.Air);
                yield return Frames(2);
                Check("prop_removed_with_support", pr.InstanceCount < before, $"props {before}->{pr.InstanceCount} at {cell} support y={supY}");
            }
            Wildlife.WildlifeManager.Enabled = false;
            wm?.Clear();
        }

        // ------------------------------------------------------------------ persistence

        IEnumerator Persistence()
        {
            if (R.NoSave || R.Save == null) { Check("persistence", false, "world has no save (run without -nosave)"); yield break; }
            var rng = new System.Random(77);
            var edits = new Dictionary<Vector3Int, (byte id, byte meta)>();
            var clusters = new List<Vector3>();
            byte[] palette = { B.Planks, B.Glass, B.Cobblestone, B.Furnace, B.Sandstone, B.LogBirch, B.Workbench, B.MossyCobblestone };
            var centres = new[] { new Vector2Int(0, 0), new Vector2Int(320, 40), new Vector2Int(-60, 640) };
            P.Flying = true;
            int setFails = 0;
            foreach (var c in centres)
            {
                var centre = new Vector3(mSpawnCol.x + c.x + 0.5f, 150, mSpawnCol.z + c.y + 0.5f);
                clusters.Add(centre);
                P.Teleport(centre);
                yield return WaitArea(centre, 3, 60f);
                int ccx = Mathf.FloorToInt(centre.x) >> 4, ccz = Mathf.FloorToInt(centre.z) >> 4;
                for (int k = 0; k < 120; k++)
                {
                    int cx = ccx + rng.Next(-3, 4), cz = ccz + rng.Next(-3, 4);
                    int x = cx * 16 + rng.Next(16), z = cz * 16 + rng.Next(16);
                    int sy = W.SurfaceY(x, z);
                    if (sy < 1) { setFails++; continue; }
                    Vector3Int pos; byte id, meta = 0;
                    if (rng.NextDouble() < 0.25) { pos = new Vector3Int(x, sy, z); id = B.Air; }        // dig
                    else
                    {
                        pos = new Vector3Int(x, sy + 1 + rng.Next(0, 4), z);
                        id = palette[rng.Next(palette.Length)];
                        if (id == B.Furnace) meta = (byte)rng.Next(1, 4);
                    }
                    if (edits.ContainsKey(pos)) continue;
                    if (!W.SetBlock(pos.x, pos.y, pos.z, id, meta) && Get(pos) != id) { setFails++; continue; }
                    edits[pos] = (id, meta);
                }
            }
            // A dig may pop a plant above; drop any recorded edit whose cell was changed by a later edit's side effect.
            foreach (var kv in edits.ToList()) if (Get(kv.Key) != kv.Value.id && W.IsLoaded(kv.Key.x, kv.Key.z)) edits.Remove(kv.Key);
            var chunksTouched = new HashSet<Vector2Int>(edits.Keys.Select(k => new Vector2Int(k.x >> 4, k.z >> 4)));
            Info($"persistence: {edits.Count} edits in {chunksTouched.Count} chunks, setFails={setFails}");

            // Unload + regenerate before saving: the first cluster is far away now.
            bool firstUnloaded = W.GetChunk(Mathf.FloorToInt(clusters[0].x) >> 4, Mathf.FloorToInt(clusters[0].z) >> 4) == null;
            yield return VerifyEdits(edits, clusters, 0, 1, "before save");
            Check("edits_survive_unload", firstUnloaded && mVerifyBad == 0, $"unloadedFirst={firstUnloaded} checked={mVerifyChecked} bad={mVerifyBad} {mVerifyNote}");

            // Player state: a little hut (roof 3 above) near spawn so "lifted to the surface" would be visible.
            var hut = new Vector3Int(mSpawnCol.x + 6, 0, mSpawnCol.z + 6);
            P.Teleport(new Vector3(hut.x + 0.5f, 150, hut.z + 0.5f));
            yield return WaitArea(P.transform.position, 2, 60f);
            int hy = W.SurfaceY(hut.x, hut.z);
            Fill(hut.x - 1, hy + 1, hut.z - 1, hut.x + 1, hy + 3, hut.z + 1, B.Air);
            Fill(hut.x - 1, hy + 4, hut.z - 1, hut.x + 1, hy + 4, hut.z + 1, B.Planks);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) W.SetBlock(hut.x + dx, hy, hut.z + dz, B.Planks);
            P.Flying = false;
            P.Teleport(new Vector3(hut.x + 0.5f, hy + 1.001f, hut.z + 0.5f));
            yield return new WaitForSeconds(0.8f);
            var inv = P.Inventory;
            inv.Clear();
            inv.Slots[0] = ItemStack.Of(B.Torch, 17);
            inv.Slots[2] = ItemStack.Of(ItemIds.PickaxeIron, 1); inv.Slots[2].Durability = 123;
            inv.Slots[5] = ItemStack.Of(ItemIds.AxeStone, 1); inv.Slots[5].Durability = 7;
            inv.Slots[10] = ItemStack.Of(B.Cobblestone, 64);
            inv.Slots[35] = ItemStack.Of(ItemIds.RawGold, 3);
            inv.Selected = 5;
            inv.Notify();
            var invSnap = (ItemStack[])inv.Slots.Clone();
            var atm = Rendering.Atmosphere.Instance;
            atm.TimeOfDay = 0.83f;
            P.Health = 13f;
            P.Yaw = 123f; P.Pitch = -20f;
            var posSaved = P.transform.position;
            var save = R.Save;
            string dir = save.Dir;
            R.SaveWorld();
            var meta0 = WorldSave.TryLoadMeta(dir);
            var pj = Json.Obj(meta0?.Meta, "player");
            Check("save_writes_player_state", pj != null && Math.Abs(Json.Num(pj, "health") - 13) < 0.01 && Math.Abs(Json.Num(meta0.Meta, "time") - 0.83) < 0.01,
                $"health={Json.Num(pj, "health")} time={Json.Num(meta0?.Meta, "time")}");
            R.QuitToMenu();
            yield return Frames(3);
            Check("quit_to_menu", R.State == GameState.MainMenu && R.World == null && R.Player == null, $"state={R.State}");
            var reloaded = WorldSave.TryLoadMeta(dir);
            R.LoadWorld(reloaded);
            while (R.State != GameState.Playing && R.State != GameState.MainMenu) yield return null;
            yield return WaitPlaying(120f);
            float hpAtReady = P != null ? P.Health : -1;
            Check("world_reloads", R.State == GameState.Playing && W != null && P != null);
            if (P == null) yield break;
            BI.ScriptedAttack = false; BI.ScriptedUse = false; P.ScriptedMove = Vector2.zero;
            Wildlife.WildlifeManager.Enabled = false;
            var ppos = P.transform.position;
            Check("position_persists_under_roof", (ppos - posSaved).magnitude < 0.25f, $"saved={posSaved:F2} now={ppos:F2} (roof at +3)");
            Check("look_persists", Mathf.Abs(Mathf.DeltaAngle(P.Yaw, 123f)) < 0.1f && Mathf.Abs(P.Pitch + 20f) < 0.1f, $"yaw={P.Yaw:F1} pitch={P.Pitch:F1}");
            bool invSame = true;
            for (int i = 0; i < Inventory.Size; i++)
                invSame &= invSnap[i].IsEmpty == P.Inventory.Slots[i].IsEmpty && (invSnap[i].IsEmpty ||
                           (invSnap[i].Id == P.Inventory.Slots[i].Id && invSnap[i].Count == P.Inventory.Slots[i].Count && invSnap[i].Durability == P.Inventory.Slots[i].Durability));
            Check("inventory_and_durability_persist", invSame && P.Inventory.Selected == 5,
                $"pick dur={P.Inventory.Slots[2].Durability} axe dur={P.Inventory.Slots[5].Durability} selected={P.Inventory.Selected}");
            Check("time_of_day_persists", Mathf.Abs(Rendering.Atmosphere.Instance.TimeOfDay - 0.83f) < 0.01f, $"tod={Rendering.Atmosphere.Instance.TimeOfDay:F3}");
            Check("health_persists", Mathf.Abs(hpAtReady - 13f) < 1.01f, $"hp={hpAtReady}");
            yield return VerifyEdits(edits, clusters, 0, clusters.Count, "after reload");
            Check("scattered_edits_persist", mVerifyBad == 0 && mVerifyChecked == edits.Count,
                $"checked={mVerifyChecked}/{edits.Count} bad={mVerifyBad} chunks={chunksTouched.Count} {mVerifyNote}");
            // Arena furnaces kept their facing (meta) too.
            if (mFurnaces.Length > 0)
            {
                P.Flying = true;
                P.Teleport(new Vector3(mA.x + 0.5f, Y0 + 1.001f, mA.z + 0.5f));
                yield return WaitArea(P.transform.position, 1, 60f);
                var metas = mFurnaces.Select(f => Get(f) == B.Furnace ? W.GetMeta(f.x, f.y, f.z).ToString() : "x").ToArray();
                Check("furnace_facing_persists", string.Join(",", metas) == "2,3,0,1", string.Join(",", metas));
                P.Flying = false;
            }

            // Dying and saving: what comes back?
            P.Damaged -= OnDamaged;
            Stand(mA);
            yield return new WaitForSeconds(0.3f);
            yield return FallFrom(6f, 1f);
            bool deadNow = P.Dead;
            R.SaveWorld();
            var dm = Json.Obj(WorldSave.TryLoadMeta(dir)?.Meta, "player");
            double savedHp = Json.Num(dm, "health", -1);
            R.QuitToMenu();
            yield return Frames(3);
            R.LoadWorld(WorldSave.TryLoadMeta(dir));
            yield return WaitPlaying(120f);
            if (P == null) yield break;
            yield return Frames(3);
            var ui = UI.UiRoot.Instance;
            bool consistent = (P.Health > 0 && !P.Dead) && !(P.Health <= 0f);
            Check("save_while_dead_reloads_alive_state", deadNow && consistent,
                $"diedBeforeSave={deadNow} savedHealth={savedHp} afterReload hp={P.Health} dead={P.Dead} deathScreen={(ui != null && ui.Top == ui.Death)}");
            BI.ScriptedAttack = false; BI.ScriptedUse = false; P.ScriptedMove = Vector2.zero;
            Restart("after save-while-dead");
        }

        int mVerifyChecked, mVerifyBad;
        string mVerifyNote;

        IEnumerator VerifyEdits(Dictionary<Vector3Int, (byte id, byte meta)> edits, List<Vector3> clusters, int from, int to, string when)
        {
            mVerifyChecked = 0; mVerifyBad = 0;
            var notes = new List<string>();
            P.Flying = true;
            for (int i = from; i < to; i++)
            {
                P.Teleport(clusters[i]);
                yield return WaitArea(clusters[i], 4, 60f);
                foreach (var kv in edits)
                {
                    if ((new Vector2(kv.Key.x, kv.Key.z) - new Vector2(clusters[i].x, clusters[i].z)).magnitude > 120f) continue;
                    if (!W.IsLoaded(kv.Key.x, kv.Key.z)) continue;
                    mVerifyChecked++;
                    byte b = Get(kv.Key), m = W.GetMeta(kv.Key.x, kv.Key.y, kv.Key.z);
                    if (b != kv.Value.id || m != kv.Value.meta)
                    {
                        mVerifyBad++;
                        if (notes.Count < 5) notes.Add($"{kv.Key}: want {Blocks.Get(kv.Value.id).Name}/{kv.Value.meta} got {Blocks.Get(b).Name}/{m}");
                    }
                }
            }
            mVerifyNote = string.Join("; ", notes);
            Info($"verify {when}: checked={mVerifyChecked} bad={mVerifyBad}");
        }

        // ------------------------------------------------------------------ respawn far from spawn

        IEnumerator RespawnFar()
        {
            P.Flying = true;
            var far = new Vector3(mSpawnCol.x + 900.5f, 160, mSpawnCol.z + 900.5f);
            P.Teleport(far);
            yield return WaitArea(far, 2, 60f);
            yield return new WaitForSeconds(1f);
            bool spawnUnloaded = !W.IsLoaded(mSpawnCol.x, mSpawnCol.z);
            P.Flying = false;
            P.Hurt(100f, "void");
            yield return Frames(3);
            var ui = UI.UiRoot.Instance;
            if (ui != null && ui.Death != null) ui.Death.Respawn(); else P.Respawn();
            var p0 = P.transform.position;
            float hpAfter = P.Health;
            float t0 = Time.time;
            yield return new WaitForSeconds(6f);
            yield return WaitArea(P.transform.position, 1, 30f);
            yield return new WaitForSeconds(1f);
            var p = P.transform.position;
            int sy = W.SurfaceY(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
            bool inside = VoxelPhysics.OverlapsVoxels(W, p + new Vector3(-0.29f, 0.01f, -0.29f), p + new Vector3(0.29f, 1.79f, 0.29f));
            bool onSurface = Mathf.Abs(p.y - (sy + 1f)) < 0.6f;
            Check("respawn_when_spawn_unloaded", spawnUnloaded && !inside && onSurface && P.Health >= hpAfter - 0.01f,
                $"spawnUnloaded={spawnUnloaded} respawnY={p0.y:F2} now={p:F2} surface={sy} insideBlocks={inside} hp={P.Health}");
            _ = t0;
        }

        // ================================================================== finish

        IEnumerator Finish()
        {
            Check("no_console_errors", mErrors.Count == 0, mErrors.Count > 0 ? $"count={mErrors.Count} first: {mErrors[0]}" : $"warnings={mWarnings}");
            foreach (var e in mErrors.Take(15)) Info("error: " + e);
            Debug.Log($"WM_QA {(mFail == 0 ? "PASS" : "FAIL")} summary pass={mPass} fail={mFail} failed=[{string.Join(",", mFailed)}]");
            Wildlife.WildlifeManager.Enabled = true;
            if (R != null && (R.State == GameState.Playing || R.State == GameState.Paused)) R.QuitToMenu();
            yield return Frames(3);
            try
            {
                if (mWorldDir != null && Directory.Exists(mWorldDir) && !Cli.Has("keepworld") && Path.GetFileName(mWorldDir).StartsWith("qatest-"))
                {
                    Directory.Delete(mWorldDir, true);
                    Info("deleted " + mWorldDir);
                }
            }
            catch (Exception e) { Info("cleanup failed: " + e.Message); }
            RestoreSettings();
            Quit(mFail == 0 ? 0 : 1);
        }

        static void Quit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(code);
#endif
        }

        // ================================================================== soak

        IEnumerator Soak()
        {
            float dur = Cli.Float("qasec", 600);
            R.NewWorld("qasoak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), Cli.Long("qaseed", 1337));
            yield return WaitPlaying(120f);
            if (P == null) { Quit(1); yield break; }
            mWorldDir = R.Save != null && !R.NoSave ? R.Save.Dir : null;
            yield return Settle(30f);
            var p = P;
            BI.ScriptedUse = false;
            p.Inventory.Clear();
            p.Inventory.Add(ItemIds.PickaxeIron, 1);
            p.Inventory.Add(B.Cobblestone, 64);
            p.Inventory.Selected = 0;
            var frames = new List<float>(4096);
            float t = 0, nextSample = 0, phaseT = 0;
            string phase = "";
            long gc0 = GC.CollectionCount(0);
            int sample = 0;
            Info($"soak start dur={dur}s nosave={R.NoSave} view={W.ViewDistance} vsync={GameSettings.Current.VSync}");
            while (t < dur && P != null)
            {
                float dt = Time.unscaledDeltaTime;
                t += dt; phaseT += dt;
                frames.Add(dt * 1000f);
                string want = (int)(t / 30f) % 3 == 0 ? "walk" : (int)(t / 30f) % 3 == 1 ? "fly" : "mine";
                if (want != phase) { phase = want; phaseT = 0; EnterSoakPhase(p, phase); }
                SoakStep(p, phase, phaseT);
                if (t >= nextSample)
                {
                    nextSample += 30f;
                    frames.Sort();
                    float avg = frames.Count > 0 ? frames.Average() : 0;
                    float p99 = frames.Count > 0 ? frames[(int)(frames.Count * 0.99f)] : 0, mx = frames.Count > 0 ? frames[frames.Count - 1] : 0;
                    int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                    int gos = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                    int mats = Resources.FindObjectsOfTypeAll<Material>().Length;
                    Debug.Log($"WM_QA_SOAK n={sample++} t={t:F0} phase={phase} monoMB={GC.GetTotalMemory(false) / 1048576f:F1} " +
                              $"unityMB={Profiler.GetTotalAllocatedMemoryLong() / 1048576f:F1} reservedMB={Profiler.GetTotalReservedMemoryLong() / 1048576f:F1} " +
                              $"gfxMB={Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576f:F1} meshes={meshes} gameObjects={gos} materials={mats} " +
                              $"chunks={W.Loaded} meshed={W.MeshedCount} verts={W.TotalVertices} drops={ItemDrop.All.Count} animals={Wildlife.WildlifeManager.Instance?.Count} " +
                              $"props={World.Decor.PropRenderer.Instance?.InstanceCount} frames={frames.Count} avgMs={avg:F2} p99Ms={p99:F2} maxMs={mx:F1} gc0={GC.CollectionCount(0) - gc0} " +
                              $"pos={p.transform.position:F0} errors={mErrors.Count}");
                    frames.Clear();
                }
                yield return null;
            }
            BI.ScriptedAttack = false;
            Check("soak_no_console_errors", mErrors.Count == 0, mErrors.Count > 0 ? $"count={mErrors.Count} first: {mErrors[0]}" : "");
            foreach (var e in mErrors.Take(10)) Info("error: " + e);
            Debug.Log($"WM_QA {(mFail == 0 ? "PASS" : "FAIL")} summary pass={mPass} fail={mFail} failed=[{string.Join(",", mFailed)}]");
            if (R.State == GameState.Playing || R.State == GameState.Paused) R.QuitToMenu();
            yield return Frames(3);
            if (mWorldDir != null && Directory.Exists(mWorldDir) && Path.GetFileName(mWorldDir).StartsWith("qasoak-")) Directory.Delete(mWorldDir, true);
            RestoreSettings();
            Quit(mFail == 0 ? 0 : 1);
        }

        void EnterSoakPhase(PlayerController p, string phase)
        {
            BI.ScriptedAttack = false;
            p.ScriptedJump = false; p.ScriptedSprint = false; p.ScriptedCrouch = false;
            if (phase == "fly") { p.Flying = true; p.Teleport(p.transform.position + Vector3.up * 3f); }
            else
            {
                p.Flying = false;
                var pos = p.transform.position;
                int sy = W.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z));
                if (sy > 0) p.Teleport(new Vector3(pos.x, sy + 1.01f, pos.z));
            }
        }

        void SoakStep(PlayerController p, string phase, float t)
        {
            switch (phase)
            {
                case "walk":
                    p.ScriptedMove = new Vector2(0, 1);
                    p.ScriptedSprint = true;
                    p.Yaw += Mathf.Sin(t * 0.4f) * 25f * Time.deltaTime;
                    p.Pitch = 5f;
                    p.ScriptedJump = Mathf.Repeat(t, 2.5f) < 0.2f;
                    break;
                case "fly":
                {
                    p.ScriptedMove = new Vector2(0, 1);
                    p.ScriptedSprint = t > 10f;
                    p.Yaw += 6f * Time.deltaTime;
                    p.Pitch = 10f;
                    var pos = p.transform.position;
                    int sy = W.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z));
                    p.ScriptedJump = sy > 0 && pos.y < sy + 20f;
                    p.ScriptedCrouch = sy > 0 && pos.y > sy + 35f;
                    break;
                }
                default: // mine: dig forward and down, place a block now and then
                    p.ScriptedMove = new Vector2(0, 0.5f);
                    p.Pitch = 35f + Mathf.Sin(t * 0.7f) * 20f;
                    p.Yaw += (Mathf.Repeat(t, 6f) < 0.3f ? 90f : 0f) * Time.deltaTime * 3f;
                    BI.ScriptedAttack = Mathf.Repeat(t, 5f) < 4.4f;
                    BI.ScriptedUse = Mathf.Repeat(t, 5f) > 4.6f;
                    p.Inventory.Selected = BI.ScriptedUse == true ? 1 : 0;
                    if (p.Inventory.Slots[1].IsEmpty) p.Inventory.Slots[1] = ItemStack.Of(B.Cobblestone, 64);
                    if (p.Inventory.Slots[0].IsEmpty) p.Inventory.Slots[0] = ItemStack.Of(ItemIds.PickaxeIron, 1);
                    break;
            }
        }

        // ================================================================== idle (external CPU sampling)

        IEnumerator Idle()
        {
            float phaseLen = Cli.Float("qaphase", 40);
            Debug.Log($"WM_QA_IDLE phase=menu t={Time.realtimeSinceStartup:F1}");
            yield return new WaitForSecondsRealtime(phaseLen);
            R.NewWorld("qaidle-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), Cli.Long("qaseed", 1337));
            yield return WaitPlaying(120f);
            mWorldDir = R.Save != null && !R.NoSave ? R.Save.Dir : null;
            yield return Settle(40f);
            P.ScriptedMove = Vector2.zero;
            BI.ScriptedAttack = false; BI.ScriptedUse = false;
            Debug.Log($"WM_QA_IDLE phase=play t={Time.realtimeSinceStartup:F1} chunks={W.Loaded}");
            float t0 = Time.realtimeSinceStartup; int f0 = Time.frameCount;
            yield return new WaitForSecondsRealtime(phaseLen);
            Info($"idle play fps={(Time.frameCount - f0) / (Time.realtimeSinceStartup - t0):F1}");
            R.Pause();
            Debug.Log($"WM_QA_IDLE phase=paused t={Time.realtimeSinceStartup:F1}");
            t0 = Time.realtimeSinceStartup; f0 = Time.frameCount;
            yield return new WaitForSecondsRealtime(phaseLen);
            Info($"idle paused fps={(Time.frameCount - f0) / (Time.realtimeSinceStartup - t0):F1}");
            R.Resume();
            R.QuitToMenu();
            Debug.Log($"WM_QA_IDLE phase=menu2 t={Time.realtimeSinceStartup:F1}");
            t0 = Time.realtimeSinceStartup; f0 = Time.frameCount;
            yield return new WaitForSecondsRealtime(phaseLen);
            Info($"idle menu fps={(Time.frameCount - f0) / (Time.realtimeSinceStartup - t0):F1}");
            Debug.Log("WM_QA_IDLE phase=end");
            if (mWorldDir != null && Directory.Exists(mWorldDir) && Path.GetFileName(mWorldDir).StartsWith("qaidle-")) Directory.Delete(mWorldDir, true);
            RestoreSettings();
            Quit(0);
        }
    }
}
