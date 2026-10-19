using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Wildmere.Items;
using Wildmere.Player;
using Wildmere.World;

namespace Wildmere.Core
{
    /// <summary>
    /// The shipping binary is the test harness (see <see cref="Cli"/>). -selftest runs scripted gameplay
    /// through the real systems and exits 0 only if every check passed; -shots/-bench/-camat capture
    /// screenshots and performance numbers from a real run.
    /// </summary>
    public sealed class Harness : MonoBehaviour
    {
        public static bool WantsAutoStart => Cli.Has("propaudit") || Cli.Has("selftest") || Cli.Has("bench") || Cli.Has("shots") || Cli.Has("camat");
        public static bool Headless => Application.isBatchMode || Cli.Has("selftest");

        readonly List<string> mErrors = new List<string>();
        int mPass, mFail;
        string mTempWorldDir;

        void OnEnable() => Application.logMessageReceived += OnLog;
        void OnDisable() => Application.logMessageReceived -= OnLog;

        void OnLog(string msg, string stack, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !msg.StartsWith("WM_SELFTEST"))
                mErrors.Add(msg.Length > 300 ? msg.Substring(0, 300) : msg);
        }

        void Start()
        {
            float quit = Cli.Float("quitafter", -1);
            if (quit > 0) StartCoroutine(QuitAfter(quit));
            if (Cli.Has("propaudit")) StartCoroutine(PropAudit());
            else if (Cli.Has("selftest")) StartCoroutine(SelfTest());
            else if (Cli.Has("bench")) StartCoroutine(Bench(Cli.Float("bench", 30)));
            else if (Cli.Has("shots") || Cli.Has("camat")) StartCoroutine(Shots());
        }

        IEnumerator QuitAfter(float s)
        {
            yield return new WaitForSecondsRealtime(s);
            Debug.Log("WM_HARNESS quitafter reached");
            Quit(0);
        }

        static void Quit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(code);
#endif
        }

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) mPass++; else mFail++;
            Debug.Log($"WM_SELFTEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        IEnumerator WaitReady(float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (GameRoot.Instance.State != GameState.Playing && Time.realtimeSinceStartup - t0 < timeout) yield return null;
        }

        IEnumerator WaitArea(Vector3 p, int radius, float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            var w = VoxelWorld.Instance;
            while (w != null && !w.ReadyAround(p, radius) && Time.realtimeSinceStartup - t0 < timeout) yield return null;
        }

        IEnumerator PropAudit()
        {
            if (Cli.Has("viamenu"))
            {
                // Like a player: the main menu's backdrop world streams in first, then a world is started.
                MenuBackdrop.ShowForced();
                yield return new WaitForSeconds(12f);
                Debug.Log($"WM_PROPAUDIT backdrop props={World.Decor.PropRenderer.Instance?.InstanceCount}");
            }
            GameRoot.Instance.StartFromCli();
            yield return WaitReady(120f);
            yield return new WaitForSeconds(8f);
            int bad = World.Decor.PropRenderer.Instance != null ? World.Decor.PropRenderer.Instance.Audit() : 0;
            Debug.Log($"WM_PROPAUDIT {(bad == 0 ? "PASS" : "FAIL")} floating={bad}");
            Quit(bad == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ self-test

        IEnumerator SelfTest()
        {
            var root = GameRoot.Instance;
            var t0 = Time.realtimeSinceStartup;
            long seed = Cli.Long("seed", 20261017);
            string name = "selftest-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            Debug.Log($"WM_SELFTEST begin seed={seed} batch={Application.isBatchMode} gfx={SystemInfo.graphicsDeviceType}");

            // 1. World generation.
            root.NewWorld(name, seed);
            yield return WaitReady(120f);
            var w = VoxelWorld.Instance;
            var p = root.Player;
            Check("world_generated", root.State == GameState.Playing && w != null && w.MeshedCount > 0,
                $"chunks={w?.Loaded} meshed={w?.MeshedCount} t={Time.realtimeSinceStartup - t0:F1}s");
            if (w == null || p == null) { Finish(); yield break; }
            mTempWorldDir = root.Save.Dir;

            // Determinism: a second generator with the same seed produces identical columns.
            var a = new Chunk(3, -2); var b = new Chunk(3, -2);
            new World.Gen.TerrainGenerator(seed).Generate(a);
            new World.Gen.TerrainGenerator(seed).Generate(b);
            bool same = true;
            for (int i = 0; i < Chunk.Volume; i++) if (a.Blocks[i] != b.Blocks[i]) { same = false; break; }
            Check("deterministic_generation", same);

            // 2. Terrain collision: standing still for 2 s keeps us on the surface.
            yield return new WaitForSeconds(2f);
            var pos0 = p.transform.position;
            int surf = w.SurfaceY(Mathf.FloorToInt(pos0.x), Mathf.FloorToInt(pos0.z));
            Check("terrain_collision", p.Grounded && pos0.y >= surf + 0.9f && pos0.y < surf + 1.3f, $"y={pos0.y:F2} surface={surf}");

            // 3. Movement: walk forward (try four headings in case a tree is in the way).
            float best = 0;
            for (int dir = 0; dir < 4 && best < 2.5f; dir++)
            {
                p.Yaw = dir * 90f;
                var start = p.transform.position;
                p.ScriptedMove = new Vector2(0, 1);
                yield return new WaitForSeconds(1.2f);
                p.ScriptedMove = Vector2.zero;
                yield return new WaitForSeconds(0.3f);
                var d = p.transform.position - start; d.y = 0;
                best = Mathf.Max(best, d.magnitude);
            }
            Check("player_moves", best > 2.5f, $"moved={best:F2}m");

            // 4. Jump.
            float baseY = p.transform.position.y, peak = baseY;
            p.ScriptedMove = Vector2.zero; p.ScriptedJump = true;
            for (float t = 0; t < 0.6f; t += Time.deltaTime) { peak = Mathf.Max(peak, p.transform.position.y); yield return null; }
            p.ScriptedJump = false;
            yield return new WaitForSeconds(0.8f);
            Check("player_jumps", peak - baseY > 0.9f && p.Grounded, $"height={peak - baseY:F2}");
            p.ScriptedMove = null;

            // 5. Mining: look straight down at the block under us with a pickaxe.
            var inv = p.Inventory;
            inv.Clear();
            inv.Add(ItemIds.PickaxeIron, 1);
            inv.Selected = 0;
            p.Pitch = 89.9f;
            yield return null;
            var feet = p.transform.position;
            var under = new Vector3Int(Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y - 0.5f), Mathf.FloorToInt(feet.z));
            byte underId = w.GetBlock(under.x, under.y, under.z);
            var bi = root.Interaction;
            bi.ScriptedAttack = true;
            float mt = 0;
            while (w.GetBlock(under.x, under.y, under.z) != B.Air && mt < 8f) { mt += Time.deltaTime; yield return null; }
            bi.ScriptedAttack = false;
            bool mined = w.GetBlock(under.x, under.y, under.z) == B.Air;
            Check("mining_breaks_block", mined, $"block={Blocks.Get(underId).Name} time={mt:F2}s");
            var minedPos = under;

            // 6. Pickup: the drop should land in the inventory (we fall onto it).
            int expect = ItemDb.DropOf(Blocks.Get(underId), ItemDb.Get(ItemIds.PickaxeIron), new System.Random(1));
            yield return new WaitForSeconds(2.0f);
            Check("item_pickup", expect == 0 || inv.Count(expect) > 0, $"item={ItemDb.NameOf(expect)} count={inv.Count(expect)}");

            // 7. Placement: stand on open ground away from the hole, place dirt ahead.
            inv.Add(B.Dirt, 10);
            for (int i = 0; i < Inventory.Hotbar; i++) if (inv.Slots[i].Id == B.Dirt) inv.Selected = i;
            for (int k = 3; k < 12; k++)
            {
                int cx = minedPos.x + k, cz = minedPos.z;
                int sy2 = w.SurfaceY(cx, cz);
                byte top = w.GetBlock(cx, sy2, cz);
                if (sy2 > 0 && top != B.Water && !Blocks.IsLeaves(top)) { p.Teleport(new Vector3(cx + 0.5f, sy2 + 1.01f, cz + 0.5f)); break; }
            }
            yield return new WaitForSeconds(0.6f);
            p.Yaw = 0; p.Pitch = 50f;
            yield return null; yield return null;
            var tgt = VoxelPhysics.Raycast(w, p.EyePosition, p.LookDir, BlockInteraction.Reach);
            bool placed = false;
            Vector3Int placedAt = default;
            if (tgt.Hit)
            {
                placedAt = Blocks.Get(tgt.Id).Replaceable && tgt.Id != B.Water ? tgt.Block : tgt.Adjacent;
                int before = inv.Count(B.Dirt);
                bi.ScriptedUse = true;
                yield return new WaitForSeconds(0.15f);
                bi.ScriptedUse = false;
                placed = w.GetBlock(placedAt.x, placedAt.y, placedAt.z) == B.Dirt && inv.Count(B.Dirt) == before - 1;
            }
            Check("block_placement", placed, $"at={placedAt} hit={tgt.Hit}");

            // 8. Placement never overlaps the player: look straight down (adjacent cell = our feet).
            p.Pitch = 89.9f;
            yield return null;
            var down = VoxelPhysics.Raycast(w, p.EyePosition, p.LookDir, BlockInteraction.Reach);
            int dirtBefore = inv.Count(B.Dirt);
            bi.ScriptedUse = true;
            yield return new WaitForSeconds(0.15f);
            bi.ScriptedUse = false;
            Check("placement_not_inside_player", !down.Hit || inv.Count(B.Dirt) == dirtBefore, $"dirt {dirtBefore}->{inv.Count(B.Dirt)}");

            // 9. Inventory logic.
            var tinv = new Inventory();
            int left = tinv.Add(B.Stone, 64 * 36 + 5);
            bool invOk = left == 5 && tinv.Count(B.Stone) == 64 * 36;
            tinv.Remove(B.Stone, 100);
            invOk &= tinv.Count(B.Stone) == 64 * 36 - 100;
            tinv.Clear();
            tinv.Add(ItemIds.PickaxeWood, 2);
            invOk &= tinv.Slots[0].Count == 1 && tinv.Slots[1].Count == 1; // tools don't stack
            Check("inventory_stacks", invOk);

            // 10. Crafting: logs -> planks -> sticks/workbench, then a station recipe.
            var cinv = new Inventory();
            cinv.Add(B.LogOak, 3);
            var none = new HashSet<Station>();
            bool c1 = Crafting.Craft(cinv, Crafting.Recipes.Find(r => r.Key == "planks_" + B.LogOak), none);
            bool c2 = Crafting.Craft(cinv, Crafting.Recipes.Find(r => r.Key == "planks_" + B.LogOak), none);
            bool c3 = Crafting.Craft(cinv, Crafting.Recipes.Find(r => r.Key == "stick"), none);
            var pick = Crafting.Recipes.Find(r => r.Key == "pickaxe_wood");
            bool noStation = !Crafting.Craft(cinv, pick, none);
            bool c4 = Crafting.Craft(cinv, pick, new HashSet<Station> { Station.Workbench });
            Check("crafting", c1 && c2 && c3 && noStation && c4 && cinv.Count(ItemIds.PickaxeWood) == 1,
                $"planks={cinv.Count(B.Planks)} sticks={cinv.Count(ItemIds.Stick)}");

            // 11. Save, close, reload: block edits, inventory and position persist.
            inv.Add(B.Glass, 7);
            int invHash = InvHash(inv);
            var savedPos = p.transform.position;
            var save = root.Save;
            root.SaveWorld();
            bool filesOk = File.Exists(Path.Combine(save.Dir, "world.json")) && File.Exists(Path.Combine(save.Dir, "mods.bin"));
            Check("world_saved", filesOk, save.Dir);
            root.QuitToMenu();
            yield return null; yield return null;
            var reloaded = WorldSave.TryLoadMeta(save.Dir);
            root.LoadWorld(reloaded);
            yield return null;
            yield return WaitReady(120f);
            w = VoxelWorld.Instance; p = root.Player;
            Check("world_reloaded", root.State == GameState.Playing && w != null && p != null);
            if (w != null && p != null)
            {
                bool minedKept = w.GetBlock(minedPos.x, minedPos.y, minedPos.z) == B.Air;
                bool placedKept = !placed || w.GetBlock(placedAt.x, placedAt.y, placedAt.z) == B.Dirt;
                Check("modifications_persist", minedKept && placedKept, $"mined={minedKept} placed={placedKept}");
                Check("inventory_persists", InvHash(p.Inventory) == invHash, $"glass={p.Inventory.Count(B.Glass)}");
                Check("position_persists", (p.transform.position - savedPos).magnitude < 1.5f, $"d={(p.transform.position - savedPos).magnitude:F2}");

                // 12. Streaming: fly far away and back; columns load and unload, memory stays bounded.
                p.Flying = true;
                long mem0 = GC.GetTotalMemory(true);
                int loaded0 = w.Loaded;
                var origin = p.transform.position;
                for (int leg = 0; leg < 2; leg++)
                {
                    var far = origin + new Vector3(leg == 0 ? 420 : 0, 0, leg == 0 ? 0 : 420);
                    p.Teleport(new Vector3(far.x, 140, far.z));
                    yield return WaitArea(p.transform.position, 2, 60f);
                    yield return new WaitForSeconds(1f);
                }
                bool farReady = w.ReadyAround(p.transform.position, 2);
                bool originUnloaded = w.GetChunk(Mathf.FloorToInt(origin.x) >> 4, Mathf.FloorToInt(origin.z) >> 4) == null;
                p.Teleport(origin + Vector3.up * 2);
                yield return WaitArea(p.transform.position, 2, 60f);
                yield return new WaitForSeconds(1f);
                long mem1 = GC.GetTotalMemory(true);
                Check("chunk_streaming", farReady && originUnloaded && w.ReadyAround(p.transform.position, 2),
                    $"loaded {loaded0}->{w.Loaded} farReady={farReady} originUnloaded={originUnloaded}");
                Check("memory_bounded", mem1 - mem0 < 400L * 1024 * 1024, $"managed {mem0 / 1048576}MB->{mem1 / 1048576}MB");
                Debug.Log($"WM_PERF selftest frames={Time.frameCount} loaded={w.Loaded} verts={w.TotalVertices} " +
                          $"gcMB={GC.GetTotalMemory(false) / 1048576} unityMB={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576}");
            }

            Finish();
        }

        static int InvHash(Inventory inv)
        {
            int h = 17;
            foreach (var s in inv.Slots) h = h * 31 + s.Id * 131 + s.Count * 7 + s.Durability;
            return h;
        }

        void Finish()
        {
            Check("no_console_errors", mErrors.Count == 0, mErrors.Count > 0 ? "first: " + mErrors[0] : "");
            Debug.Log($"WM_SELFTEST {(mFail == 0 ? "PASS" : "FAIL")} summary pass={mPass} fail={mFail}");
            try
            {
                if (GameRoot.Instance != null) GameRoot.Instance.QuitToMenu();
                if (mTempWorldDir != null && Directory.Exists(mTempWorldDir) && !Cli.Has("keepworld")) Directory.Delete(mTempWorldDir, true);
            }
            catch (Exception e) { Debug.LogWarning($"WM_SELFTEST cleanup: {e.Message}"); }
            Quit(mFail == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ screenshots

        IEnumerator Shots()
        {
            var root = GameRoot.Instance;
            root.StartFromCli();
            yield return WaitReady(120f);
            var p = root.Player;
            var camAt = Cli.Vec("camat");
            if (Cli.Has("camwild") && p != null && Wildlife.WildlifeManager.Instance != null &&
                Enum.TryParse(Cli.Str("camwild", "Deer"), true, out Wildlife.Species sp))
            {
                // Put three animals in front of the camera on open ground.
                var fwd = Quaternion.Euler(0, p.Yaw, 0) * Vector3.forward;
                for (int k = 0; k < 3; k++)
                {
                    var at = p.transform.position + fwd * (5f + k * 1.5f) + Quaternion.Euler(0, p.Yaw, 0) * Vector3.right * (k - 1) * 1.6f;
                    int sy = VoxelWorld.Instance.SurfaceY(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.z));
                    Wildlife.WildlifeManager.Instance.SpawnAt(sp, new Vector3(at.x, sy + 1f + (sp == Wildlife.Species.Songbird ? 0f : 0f), at.z));
                }
                p.Pitch = 12f;
            }
            if (Cli.Has("camprop") && p != null)
            {
                var near = World.Decor.PropRenderer.Instance?.Nearest(p.transform.position, Cli.Str("camprop"));
                if (near.HasValue)
                {
                    camAt = near.Value + new Vector3(3.2f, 2.0f, 3.2f);
                    Debug.Log($"WM_HARNESS camprop at {near.Value}");
                    p.Flying = true;
                    p.Teleport(camAt.Value - Vector3.up * PlayerController.Eye);
                    var d = (near.Value + Vector3.up * 0.6f - camAt.Value).normalized;
                    p.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    p.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
                    p.ScriptedMove = Vector2.zero;
                    yield return WaitArea(p.transform.position, 3, 60f);
                    camAt = null;
                }
            }
            if (camAt.HasValue && p != null)
            {
                p.Flying = true;
                p.Teleport(camAt.Value - Vector3.up * PlayerController.Eye);
                var look = Cli.Vec("camlook");
                if (look.HasValue)
                {
                    var d = (look.Value - camAt.Value).normalized;
                    p.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    p.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
                }
                p.ScriptedMove = Vector2.zero;
                yield return WaitArea(p.transform.position, Mathf.Min(6, VoxelWorld.Instance.ViewDistance), 90f);
            }
            if (Cli.Has("autoplay") && p != null) StartCoroutine(AutoPlay(p));
            if (Cli.Has("hold") && p != null && int.TryParse(Cli.Str("hold"), out int holdId) && ItemDb.Exists(holdId))
            {
                p.Inventory.Slots[0] = ItemStack.Of(holdId, 1);
                p.Inventory.Selected = 0;
                p.Inventory.Notify();
            }

            string dir = Cli.Str("shotdir", Path.Combine(Application.persistentDataPath, "Screenshots"));
            Directory.CreateDirectory(dir);
            var times = Cli.Floats("shots");
            if (times.Count == 0) times.Add(3f);
            float start = Time.realtimeSinceStartup;
            int n = 0;
            foreach (var t in times)
            {
                while (Time.realtimeSinceStartup - start < t) yield return null;
                yield return new WaitForEndOfFrame();
                string path = Path.Combine(dir, $"shot_{n++:00}_{t:0}s.png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"WM_PROPS instances={World.Decor.PropRenderer.Instance?.InstanceCount}");
                Debug.Log($"WM_SHOT {path} fps={1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F0} chunks={VoxelWorld.Instance?.Loaded}");
            }
            yield return new WaitForSecondsRealtime(1f);
            if (!Cli.Has("quitafter")) Quit(0);
        }

        IEnumerator AutoPlay(PlayerController p)
        {
            // Wander: walk, turn and jump so screenshots show motion and streaming.
            p.Flying = false;
            float t = 0;
            while (p != null)
            {
                t += Time.deltaTime;
                p.ScriptedMove = new Vector2(0, 1);
                p.ScriptedSprint = true;
                p.Yaw += Mathf.Sin(t * 0.4f) * 25f * Time.deltaTime;
                p.ScriptedJump = Mathf.Repeat(t, 2.5f) < 0.2f;
                yield return null;
            }
        }

        // ------------------------------------------------------------------ benchmark

        IEnumerator Bench(float seconds)
        {
            var root = GameRoot.Instance;
            root.StartFromCli();
            yield return WaitReady(120f);
            var p = root.Player;
            var w = VoxelWorld.Instance;
            p.Flying = true;
            var start = p.transform.position + Vector3.up * 12f;
            p.Teleport(start);
            yield return WaitArea(start, Mathf.Min(w.ViewDistance, 8), 120f);
            yield return new WaitForSecondsRealtime(3f);

            var frames = new List<float>(8192);
            float t = 0, worst = 0;
            float speed = Cli.Float("benchspeed", 8f);
            long gc0 = GC.CollectionCount(0);
            while (t < seconds)
            {
                float dt = Time.unscaledDeltaTime;
                t += dt;
                frames.Add(dt * 1000f);
                worst = Mathf.Max(worst, dt);
                if (dt > 0.035f)
                    Debug.Log($"WM_SPIKE t={t:F2}s ms={dt * 1000f:F1} gc0={GC.CollectionCount(0) - gc0} gc1={GC.CollectionCount(1)} chunks={w.Loaded} meshed={w.MeshedCount} genQ={w.GenInFlight} meshQ={w.MeshInFlight} mainQ={w.Pool.MainPending}");
                // A gentle curving flight at walking-ish speed with a slow look-around.
                p.Yaw = 30f + Mathf.Sin(t * 0.15f) * 60f;
                p.Pitch = 8f + Mathf.Sin(t * 0.33f) * 6f;
                var fwd = Quaternion.Euler(0, p.Yaw, 0) * Vector3.forward;
                var pos = p.transform.position + fwd * speed * dt;
                int sy = w.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z));
                if (sy > 0) pos.y = Mathf.Lerp(pos.y, sy + 10f, dt * 0.8f);
                p.transform.position = pos;
                p.Velocity = Vector3.zero;
                yield return null;
            }
            frames.Sort();
            float avg = 0; foreach (var f in frames) avg += f; avg /= frames.Count;
            float p50 = frames[frames.Count / 2], p95 = frames[(int)(frames.Count * 0.95f)], p99 = frames[(int)(frames.Count * 0.99f)];
            Debug.Log($"WM_PERF bench preset={GameSettings.Current.Preset} view={w.ViewDistance} res={Screen.width}x{Screen.height} scale={GameSettings.Current.RenderScale} " +
                      $"frames={frames.Count} avgMs={avg:F2} avgFps={1000f / avg:F1} p50Ms={p50:F2} p95Ms={p95:F2} p99Ms={p99:F2} worstMs={worst * 1000f:F1} " +
                      $"low1Fps={1000f / p99:F1} chunks={w.Loaded} meshed={w.MeshedCount} verts={w.TotalVertices} gc0={GC.CollectionCount(0) - gc0} " +
                      $"unityMB={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576} monoMB={GC.GetTotalMemory(false) / 1048576}");
            if (!Cli.Has("quitafter")) Quit(0);
        }
    }
}
