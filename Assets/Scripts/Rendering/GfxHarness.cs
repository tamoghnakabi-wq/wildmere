using System.Collections;
using UnityEngine;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Graphics viewpoints for the harness (shipping binary):
    ///   -camwater          stand on the nearest lake/sea shore and look across the water
    ///   -camforest         stand inside the nearest dark forest (or densest forest), looking towards the sun
    ///   -camcave           stand in the nearest open cave mouth / underground pocket below the surface
    ///   -camunderwater     dive 3 m under the nearest deep water, looking along the bottom
    /// Used with -gfxshots (offscreen) or -shots. Logs WM_GFXCAM with the chosen spot.
    /// </summary>
    public sealed class GfxHarness : MonoBehaviour
    {
        public static bool Wanted => Cli.Has("camwater") || Cli.Has("camforest") || Cli.Has("camcave") || Cli.Has("camunderwater");

        IEnumerator Start()
        {
            var root = GameRoot.Instance;
            while (root == null || root.State != GameState.Playing || root.Player == null) { root = GameRoot.Instance; yield return null; }
            var p = root.Player;
            var w = VoxelWorld.Instance;
            Vector3 origin = p.transform.position;
            if (Cli.Has("camforest")) yield return Forest(p, w, origin);
            else if (Cli.Has("camcave")) yield return Cave(p, w, origin);
            else yield return Water(p, w, origin, Cli.Has("camunderwater"));
        }

        static void Place(Player.PlayerController p, Vector3 eye, Vector3 look)
        {
            p.Flying = true;
            p.Teleport(eye - Vector3.up * Player.PlayerController.Eye);
            var d = (look - eye).normalized;
            p.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            p.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            p.ScriptedMove = Vector2.zero;
            p.Velocity = Vector3.zero;
        }

        IEnumerator WaitLoaded(VoxelWorld w, Vector3 at)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!w.ReadyAround(at, 2) && Time.realtimeSinceStartup - t0 < 40f) yield return null;
        }

        IEnumerator Water(Player.PlayerController p, VoxelWorld w, Vector3 origin, bool dive)
        {
            var gen = w.Gen;
            // Spiral over biome samples for water with some depth (sea level 62).
            Vector3? found = null;
            for (int r = 8; r < 900 && found == null; r += 8)
                for (int a = 0; a < 24 && found == null; a++)
                {
                    float ang = a / 24f * Mathf.PI * 2f + r * 0.1f;
                    int x = Mathf.FloorToInt(origin.x + Mathf.Cos(ang) * r), z = Mathf.FloorToInt(origin.z + Mathf.Sin(ang) * r);
                    if (gen is World.Gen.TerrainGenerator tg)
                    {
                        var c = tg.SampleColumn(x, z);
                        var c2 = tg.SampleColumn(x + 8, z + 8);
                        if (c.Water > 0 && c.Height <= c.Water - 3 && c2.Water > 0 && c2.Height <= c2.Water - 2) found = new Vector3(x, c.Water, z);
                    }
                    else
                    {
                        var s = gen.SampleBiome(x, z);
                        if (s.Biome == Biome.Ocean || s.Biome == Biome.River) found = new Vector3(x, 62, z);
                    }
                }
            if (found == null) { Debug.Log("WM_GFXCAM water not found"); yield break; }
            var water = found.Value;
            var dir = water - origin; dir.y = 0; dir = dir.sqrMagnitude > 1 ? dir.normalized : Vector3.forward;
            Place(p, water + Vector3.up * 30f, water + dir * 10f);
            yield return WaitLoaded(w, water);
            // Walk back towards the origin until we find the shore, then stand a few metres onto land.
            Vector3 eye = water - dir * 14f + Vector3.up * 4.5f;
            for (int k = 0; k < 120; k++)
            {
                var q = water - dir * k;
                int sy = w.SurfaceY(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.z));
                if (sy >= water.y && w.GetBlock(Mathf.FloorToInt(q.x), sy, Mathf.FloorToInt(q.z)) != B.Water)
                {
                    eye = q - dir * 3f; eye.y = Mathf.Max(sy, water.y) + 1f + Player.PlayerController.Eye + 1.5f;
                    break;
                }
            }
            if (dive)
            {
                // Mid-depth over the deepest nearby column.
                Vector3 best = water; int bestDepth = 0;
                for (int dx = -6; dx <= 6; dx += 2)
                    for (int dz = -6; dz <= 6; dz += 2)
                    {
                        int x = Mathf.FloorToInt(water.x) + dx, z = Mathf.FloorToInt(water.z) + dz, y = (int)water.y, d = 0;
                        while (y - d > 2 && w.GetBlock(x, y - d, z) == B.Water) d++;
                        if (d > bestDepth) { bestDepth = d; best = new Vector3(x + 0.5f, y, z + 0.5f); }
                    }
                eye = best + Vector3.up * (0.6f - bestDepth * 0.5f);
                Place(p, eye, eye + dir * 10f + Vector3.up * 0.8f);
                Debug.Log($"WM_GFXCAM underwater depth={bestDepth}");
            }
            else Place(p, eye, water + dir * 25f + Vector3.up * -2f);
            Debug.Log($"WM_GFXCAM water eye={eye} water={water}");
        }

        IEnumerator Forest(Player.PlayerController p, VoxelWorld w, Vector3 origin)
        {
            var gen = w.Gen;
            Vector3 best = origin; float bestScore = -1;
            for (int r = 0; r < 1600 && bestScore < 0.95f; r += 16)
                for (int a = 0; a < 32; a++)
                {
                    float ang = a / 32f * Mathf.PI * 2f;
                    int x = Mathf.FloorToInt(origin.x + Mathf.Cos(ang) * r), z = Mathf.FloorToInt(origin.z + Mathf.Sin(ang) * r);
                    var s = gen.SampleBiome(x, z);
                    float score = (s.Biome == Biome.DarkForest ? 1f : 0f) * 0.5f + s.Darkness * 0.5f + s.Forest * 0.2f - r / 20000f;
                    if (s.Height < 64) continue;
                    if (score > bestScore) { bestScore = score; best = new Vector3(x + 0.5f, s.Height, z + 0.5f); }
                }
            Place(p, best + Vector3.up * 30f, best + Vector3.forward);
            yield return WaitLoaded(w, best);
            int sy = w.SurfaceY(Mathf.FloorToInt(best.x), Mathf.FloorToInt(best.z));
            // Inside the canopy, find the ground under the leaves.
            int gy = sy;
            for (int y = sy; y > 40; y--)
            {
                byte b = w.GetBlock(Mathf.FloorToInt(best.x), y, Mathf.FloorToInt(best.z));
                if (Blocks.IsOpaque[b] && !Blocks.IsLeaves(b) && b != B.LogOak && b != B.LogBirch && b != B.LogSpruce && b != B.LogDark) { gy = y; break; }
            }
            var eye = new Vector3(best.x, gy + 1f + Player.PlayerController.Eye, best.z);
            var sun = Atmosphere.SunDirection(Atmosphere.Instance != null ? Atmosphere.Instance.TimeOfDay : 0.3f);
            var flat = new Vector3(sun.x, 0, sun.z).normalized;
            Place(p, eye, eye + flat * 10f + Vector3.up * 2.2f);
            Debug.Log($"WM_GFXCAM forest eye={eye} score={bestScore:F2} biome={gen.SampleBiome(Mathf.FloorToInt(best.x), Mathf.FloorToInt(best.z)).Biome}");
        }

        // A real cave room: mostly air in a 7 x 3 x 7 box around the spot.
        static bool OpenCave(VoxelWorld w, int x, int y, int z)
        {
            int air = 0;
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = -3; dz <= 3; dz++)
                    for (int dy = 0; dy <= 2; dy++)
                        if (w.GetBlock(x + dx, y + dy, z + dz) == B.Air) air++;
            return air >= 110;
        }

        IEnumerator Cave(Player.PlayerController p, VoxelWorld w, Vector3 origin)
        {
            yield return WaitLoaded(w, origin);
            // Find an air pocket 8+ blocks under the surface within the loaded area.
            for (int r = 0; r < 120; r += 2)
                for (int a = 0; a < 16; a++)
                {
                    float ang = a / 16f * Mathf.PI * 2f;
                    int x = Mathf.FloorToInt(origin.x + Mathf.Cos(ang) * r), z = Mathf.FloorToInt(origin.z + Mathf.Sin(ang) * r);
                    int sy = w.SurfaceY(x, z);
                    if (sy < 0) continue;
                    if (w.Gen is World.Gen.TerrainGenerator tg) sy = Mathf.Min(sy, tg.SampleColumn(x, z).Height);   // ignore trees
                    for (int y = sy - 8; y > 12; y--)
                    {
                        if (w.GetBlock(x, y, z) == B.Air && w.GetBlock(x, y + 1, z) == B.Air && Blocks.IsOpaque[w.GetBlock(x, y - 1, z)] && OpenCave(w, x, y, z))
                        {
                            // Light it with a few torches so the cave lighting is visible.
                            var eye = new Vector3(x + 0.5f, y + Player.PlayerController.Eye, z + 0.5f);
                            for (int k = 0; k < 4; k++)
                            {
                                int tx = x + (k == 0 ? 3 : k == 1 ? -3 : 0), tz = z + (k == 2 ? 3 : k == 3 ? -3 : 0);
                                for (int ty = y + 2; ty >= y - 3; ty--)
                                    if (w.GetBlock(tx, ty, tz) == B.Air && Blocks.IsOpaque[w.GetBlock(tx, ty - 1, tz)]) { w.SetBlock(tx, ty, tz, B.Torch); break; }
                            }
                            Place(p, eye, eye + new Vector3(1, -0.15f, 0.4f) * 8f);
                            Debug.Log($"WM_GFXCAM cave eye={eye}");
                            yield break;
                        }
                    }
                }
            Debug.Log("WM_GFXCAM cave not found");
        }
    }
}
