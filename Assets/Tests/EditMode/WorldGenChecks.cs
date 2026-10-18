using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Wildmere.World;
using Wildmere.World.Gen;

namespace Wildmere.Tests
{
    /// <summary>
    /// World-generation checks shared by the EditMode tests (WorldGenTests) and the standalone Mono harness in
    /// art-source/worldgen/harness. Pure C#: every check returns null when it passes, or a failure message.
    /// </summary>
    public static class WorldGenChecks
    {
        public static Chunk Gen(TerrainGenerator g, int cx, int cz) { var c = new Chunk(cx, cz); g.Generate(c); return c; }

        public static string SameChunk(Chunk a, Chunk b)
        {
            for (int i = 0; i < Chunk.Volume; i++)
                if (a.Blocks[i] != b.Blocks[i]) return $"block {i} differs ({a.Blocks[i]} vs {b.Blocks[i]}) in chunk {a.CX},{a.CZ}";
            for (int i = 0; i < 256; i++)
            {
                if (a.Biome[i] != b.Biome[i]) return $"biome {i} differs";
                if (!a.GrassTint[i].Equals(b.GrassTint[i]) || !a.FoliageTint[i].Equals(b.FoliageTint[i])) return $"tint {i} differs";
            }
            if (a.Props.Count != b.Props.Count) return $"prop count {a.Props.Count} vs {b.Props.Count}";
            for (int i = 0; i < a.Props.Count; i++)
            {
                var p = a.Props[i]; var q = b.Props[i];
                if (p.X != q.X || p.Y != q.Y || p.Z != q.Z || p.Kind != q.Kind || p.Yaw != q.Yaw || p.Scale != q.Scale) return $"prop {i} differs";
            }
            return null;
        }

        /// <summary>Chunk coordinates spread over a wide area (different biomes).</summary>
        public static List<(int, int)> SpreadChunks(int n, int seedSalt = 0)
        {
            var list = new List<(int, int)>();
            var r = new SplitMix(977 + seedSalt);
            for (int i = 0; i < n; i++) list.Add((r.Range(-160, 160), r.Range(-160, 160)));
            return list;
        }

        public static string Determinism(long seed, List<(int, int)> chunks)
        {
            var g1 = new TerrainGenerator(seed);
            var g2 = new TerrainGenerator(seed);
            // generate in different orders so caches cannot mask order dependence
            var a = new Chunk[chunks.Count];
            for (int i = 0; i < chunks.Count; i++) a[i] = Gen(g1, chunks[i].Item1, chunks[i].Item2);
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                var b = Gen(g2, chunks[i].Item1, chunks[i].Item2);
                var e = SameChunk(a[i], b);
                if (e != null) return e;
            }
            return null;
        }

        public static string ParallelEqualsSerial(long seed, List<(int, int)> chunks, int threads)
        {
            var serial = new Chunk[chunks.Count];
            var gs = new TerrainGenerator(seed);
            for (int i = 0; i < chunks.Count; i++) serial[i] = Gen(gs, chunks[i].Item1, chunks[i].Item2);
            var gp = new TerrainGenerator(seed);
            var par = new Chunk[chunks.Count];
            int next = -1;
            Exception err = null;
            var ts = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ts[t] = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            int k = Interlocked.Increment(ref next);
                            if (k >= chunks.Count) break;
                            par[k] = Gen(gp, chunks[k].Item1, chunks[k].Item2);
                        }
                    }
                    catch (Exception e) { err = e; }
                });
                ts[t].Start();
            }
            foreach (var t in ts) t.Join();
            if (err != null) return "exception on worker: " + err;
            for (int i = 0; i < chunks.Count; i++)
            {
                var e = SameChunk(serial[i], par[i]);
                if (e != null) return e;
            }
            return null;
        }

        /// <summary>A 3x3 block of chunks around (cx, cz) for cross-border checks.</summary>
        public sealed class Area
        {
            public readonly Chunk[] C = new Chunk[9];
            public readonly int CX, CZ;
            public Area(TerrainGenerator g, int cx, int cz)
            {
                CX = cx; CZ = cz;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++) C[(dx + 1) + (dz + 1) * 3] = Gen(g, cx + dx, cz + dz);
            }
            public int X0 => (CX - 1) * 16;
            public int Z0 => (CZ - 1) * 16;
            public byte Get(int x, int y, int z)
            {
                int ax = x - X0, az = z - Z0;
                if (ax < 0 || az < 0 || ax >= 48 || az >= 48 || y < 0 || y >= Chunk.SY) return 255;
                return C[(ax >> 4) + (az >> 4) * 3].Blocks[(ax & 15) + ((az & 15) << 4) + (y << 8)];
            }
        }

        static bool IsLog(byte b) => b >= B.LogOak && b <= B.LogDark;
        static bool IsLeaf(byte b) => b >= B.LeavesOak && b <= B.LeavesDark;
        static bool Ground(byte b) => b != 255 && Blocks.IsSolid[b] && !IsLeaf(b) && !IsLog(b) && b != B.Cactus;

        /// <summary>Trees overlapping two adjacent chunks must be decided identically by both.</summary>
        public static string TreeDecisionsAgree(TerrainGenerator g, int cx, int cz, out int shared)
        {
            shared = 0;
            var a = new List<TerrainGenerator.Tree>();
            var b = new List<TerrainGenerator.Tree>();
            g.CollectTrees(cx, cz, a);
            foreach (var (dx, dz) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1) })
            {
                g.CollectTrees(cx + dx, cz + dz, b);
                var setB = new HashSet<(int, int, int, int)>();
                foreach (var t in b) setB.Add((t.X, t.Y, t.Z, (int)t.Type));
                int bx = (cx + dx) * 16, bz = (cz + dz) * 16;
                foreach (var t in a)
                {
                    bool reachesB = t.X + TerrainGenerator.TreeReach >= bx && t.X - TerrainGenerator.TreeReach <= bx + 15
                                 && t.Z + TerrainGenerator.TreeReach >= bz && t.Z - TerrainGenerator.TreeReach <= bz + 15;
                    if (!reachesB) continue;
                    shared++;
                    if (!setB.Contains((t.X, t.Y, t.Z, (int)t.Type))) return $"tree {t.Type} at {t.X},{t.Y},{t.Z} decided by chunk {cx},{cz} but not by {cx + dx},{cz + dz}";
                }
            }
            return null;
        }

        /// <summary>Block-level tree checks on the centre chunk of a 3x3 area: no orphan leaves, no floating or wet trunks.</summary>
        public static string TreeBlocks(Area ar, out int logs, out int leaves)
        {
            logs = 0; leaves = 0;
            int x0 = ar.CX * 16, z0 = ar.CZ * 16;
            for (int z = z0; z < z0 + 16; z++)
                for (int x = x0; x < x0 + 16; x++)
                    for (int y = 1; y < Chunk.SY - 1; y++)
                    {
                        byte b = ar.Get(x, y, z);
                        if (IsLeaf(b))
                        {
                            leaves++;
                            bool found = false;
                            for (int dz = -7; dz <= 7 && !found; dz++)
                                for (int dx = -7; dx <= 7 && !found; dx++)
                                    for (int dy = -4; dy <= 3 && !found; dy++)
                                        if (IsLog(ar.Get(x + dx, y + dy, z + dz))) found = true;
                            if (!found) return $"orphan leaf at {x},{y},{z}";
                        }
                        else if ((IsLog(b) || b == B.Cactus) && !IsLog(ar.Get(x, y - 1, z)) && ar.Get(x, y - 1, z) != B.Cactus)
                        {
                            logs++;
                            byte below = ar.Get(x, y - 1, z);
                            int run = 0;
                            while (ar.Get(x, y + run, z) == b) run++;
                            if (run >= 3 || b == B.Cactus)
                            {
                                // trunk base: must stand on dry ground
                                if (!Ground(below)) return $"trunk at {x},{y},{z} stands on block {below}";
                                if (ar.Get(x, y, z) == B.Water) return $"trunk in water at {x},{y},{z}";
                            }
                            for (int k = 0; k < run; k++)
                            {
                                foreach (var (nx, nz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                                    if (ar.Get(x + nx, y + k, z + nz) == B.Water && run >= 3 && k == 0) return $"trunk next to water at {x},{y},{z}";
                            }
                        }
                    }
            return null;
        }

        /// <summary>Plants on suitable ground with air (or another plant) above; props on solid ground in air.</summary>
        public static string VegetationAndProps(Area ar, out int plants, out int props)
        {
            plants = 0; props = 0;
            int x0 = ar.CX * 16, z0 = ar.CZ * 16;
            for (int z = z0; z < z0 + 16; z++)
                for (int x = x0; x < x0 + 16; x++)
                    for (int y = 1; y < Chunk.SY - 1; y++)
                    {
                        byte b = ar.Get(x, y, z);
                        if (!Blocks.IsPlant(b) || b == B.Torch) continue;
                        plants++;
                        byte below = ar.Get(x, y - 1, z);
                        bool ok = below == B.Grass || below == B.Podzol || below == B.SnowyGrass || below == B.Dirt || below == B.Sand
                                  || (b == B.GlowCap || b == B.MushroomBrown || b == B.MushroomRed) && Ground(below);
                        if (!ok) return $"plant {b} at {x},{y},{z} on block {below}";
                    }
            var centre = ar.C[4];
            var cells = new HashSet<(int, int, int)>();
            foreach (var p in centre.Props)
            {
                props++;
                if (p.Kind >= PropCatalog.Names.Length) return $"prop kind {p.Kind} out of range";
                if (p.X < x0 || p.X >= x0 + 16 || p.Z < z0 || p.Z >= z0 + 16) return $"prop {PropCatalog.Names[p.Kind]} anchored outside its chunk";
                byte at = ar.Get(p.X, p.Y, p.Z), below = ar.Get(p.X, p.Y - 1, p.Z);
                if (at != B.Air) return $"prop {PropCatalog.Names[p.Kind]} at {p.X},{p.Y},{p.Z} anchor is block {at}";
                if (p.Kind == PropCatalog.LilyPad) { if (below != B.Water) return $"lily pad not on water at {p.X},{p.Y},{p.Z}"; }
                else if (!Ground(below)) return $"prop {PropCatalog.Names[p.Kind]} at {p.X},{p.Y},{p.Z} stands on {below}";
                if (!cells.Add((p.X, p.Y, p.Z))) return $"two props share anchor {p.X},{p.Y},{p.Z}";
            }
            return null;
        }

        public static string OreRanges(TerrainGenerator g, List<(int, int)> chunks, out int coal, out int iron, out int gold)
        {
            coal = iron = gold = 0;
            foreach (var (cx, cz) in chunks)
            {
                var c = Gen(g, cx, cz);
                for (int y = 0; y < Chunk.SY; y++)
                    for (int i = 0; i < 256; i++)
                    {
                        byte b = c.Blocks[i + (y << 8)];
                        if (b == B.CoalOre) { coal++; if (y < TerrainGenerator.CoalMinY || y > TerrainGenerator.CoalMaxY) return $"coal at y={y}"; }
                        else if (b == B.IronOre) { iron++; if (y < TerrainGenerator.IronMinY || y > TerrainGenerator.IronMaxY) return $"iron at y={y}"; }
                        else if (b == B.GoldOre) { gold++; if (y < TerrainGenerator.GoldMinY || y > TerrainGenerator.GoldMaxY) return $"gold at y={y}"; }
                        else if (b == B.Bedrock && y > 3) return $"bedrock at y={y}";
                        if (y == 0 && b != B.Bedrock) return "missing bedrock floor";
                    }
            }
            if (coal == 0 || iron == 0 || gold == 0) return $"missing ores coal={coal} iron={iron} gold={gold}";
            return null;
        }

        /// <summary>Columns where a cave breaks through the terrain surface to open sky.</summary>
        public static int CaveOpenings(TerrainGenerator g, List<(int, int)> chunks)
        {
            int open = 0;
            foreach (var (cx, cz) in chunks)
            {
                var c = Gen(g, cx, cz);
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++)
                    {
                        var s = g.SampleColumn(cx * 16 + x, cz * 16 + z);
                        if (s.Water >= 0) continue;
                        int h = s.Height;
                        // the terrain surface block and the two below it were carved, and the sky is open above
                        int col = x + (z << 4);
                        if (c.Blocks[col + (h << 8)] != B.Air || c.Blocks[col + ((h - 1) << 8)] != B.Air || c.Blocks[col + ((h - 2) << 8)] != B.Air) continue;
                        bool sky = true;
                        for (int y = h + 1; y < Chunk.SY && sky; y++) { byte b = c.Blocks[col + (y << 8)]; if (b != B.Air && !Blocks.IsPlant(b)) sky = false; }
                        if (sky) open++;
                    }
            }
            return open;
        }

        /// <summary>No cave air directly under or beside water (oceans, rivers and lakes must not drain/flood).</summary>
        public static string SealedWater(Area ar)
        {
            int x0 = ar.CX * 16, z0 = ar.CZ * 16;
            for (int z = z0; z < z0 + 16; z++)
                for (int x = x0; x < x0 + 16; x++)
                    for (int y = 10; y < Chunk.SY - 1; y++)
                    {
                        if (ar.Get(x, y, z) != B.Water) continue;
                        if (ar.Get(x, y - 1, z) == B.Air) return $"air under water at {x},{y - 1},{z}";
                        foreach (var (nx, nz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            byte n = ar.Get(x + nx, y, z + nz);
                            if (n == B.Air) return $"air beside water at {x + nx},{y},{z + nz} (water at {x},{y},{z})";
                        }
                    }
            return null;
        }

        public static Dictionary<Biome, double> Coverage(TerrainGenerator g, int half, int step)
        {
            var counts = new Dictionary<Biome, int>();
            int total = 0;
            for (int z = -half; z < half; z += step)
                for (int x = -half; x < half; x += step)
                {
                    var b = g.SampleColumn(x, z).Biome;
                    counts.TryGetValue(b, out int c); counts[b] = c + 1; total++;
                }
            var res = new Dictionary<Biome, double>();
            foreach (var kv in counts) res[kv.Key] = (double)kv.Value / total;
            return res;
        }

        public static string CoverageVerdict(Dictionary<Biome, double> cov)
        {
            foreach (var b in new[] { Biome.TemperateForest, Biome.SnowyMountains, Biome.Desert, Biome.Plains, Biome.DarkForest, Biome.Ocean, Biome.River, Biome.Beach })
                if (!cov.ContainsKey(b) || cov[b] <= 0) return $"biome {b} never occurs";
            foreach (var kv in cov) if (kv.Value > 0.45) return $"biome {kv.Key} covers {kv.Value:P0}";
            return null;
        }

        public static double MsPerChunk(TerrainGenerator g, int n)
        {
            for (int i = 0; i < 8; i++) Gen(g, 400 + i, -300);   // JIT warm-up
            var list = SpreadChunks(n, 5);
            var sw = Stopwatch.StartNew();
            foreach (var (cx, cz) in list) Gen(g, cx, cz);
            return sw.Elapsed.TotalMilliseconds / n;
        }

        public static string SpawnOk(TerrainGenerator g, out UnityEngine.Vector3Int sp)
        {
            sp = g.FindSpawn();
            var ar = new Area(g, sp.x >> 4, sp.z >> 4);
            byte feet = ar.Get(sp.x, sp.y, sp.z), head = ar.Get(sp.x, sp.y + 1, sp.z), below = ar.Get(sp.x, sp.y - 1, sp.z);
            if (feet != B.Air && !Blocks.IsPlant(feet)) return $"spawn feet in block {feet} at {sp}";
            if (head != B.Air) return $"spawn head in block {head}";
            if (!Ground(below) || below == B.Ice) return $"spawn stands on block {below}";
            for (int y = sp.y; y < sp.y + 12; y++) if (IsLeaf(ar.Get(sp.x, y, sp.z)) || IsLog(ar.Get(sp.x, y, sp.z))) return "spawn under a tree";
            return null;
        }

        /// <summary>Find a chunk whose centre column has the given biome (spiral search).</summary>
        public static bool FindBiomeChunk(TerrainGenerator g, Biome b, out int cx, out int cz)
        {
            for (int r = 0; r < 300; r += 3)
                for (int k = 0; k < 8 * Math.Max(1, r); k += 3)
                {
                    double a = k * Math.PI * 2 / (8 * Math.Max(1, r));
                    cx = (int)Math.Round(Math.Cos(a) * r); cz = (int)Math.Round(Math.Sin(a) * r);
                    int hits = 0;
                    for (int q = 0; q < 4; q++) if (g.SampleColumn(cx * 16 + 4 + (q & 1) * 8, cz * 16 + 4 + (q >> 1) * 8).Biome == b) hits++;
                    if (hits == 4) return true;
                }
            cx = cz = 0;
            return false;
        }

        /// <summary>All block-level checks for the 3x3 areas around one chunk of each biome. Returns a report; failures start with FAIL.</summary>
        public static string BiomeAreaReport(TerrainGenerator g, StringBuilder log)
        {
            string first = null;
            foreach (var b in new[] { Biome.TemperateForest, Biome.DarkForest, Biome.Plains, Biome.Desert, Biome.SnowyMountains, Biome.River, Biome.Ocean, Biome.Beach })
            {
                if (!FindBiomeChunk(g, b, out int cx, out int cz)) { log?.AppendLine($"no chunk found for {b}"); continue; }
                var ar = new Area(g, cx, cz);
                string e1 = TreeBlocks(ar, out int logs, out int leaves);
                string e2 = VegetationAndProps(ar, out int plants, out int props);
                string e3 = SealedWater(ar);
                string e4 = TreeDecisionsAgree(g, cx, cz, out int shared);
                log?.AppendLine($"{b,-16} chunk {cx},{cz}: trunks={logs} leaves={leaves} plants={plants} props={props} sharedTrees={shared} " +
                                $"{e1 ?? "trees ok"}; {e2 ?? "veg ok"}; {e3 ?? "water sealed"}; {e4 ?? "decisions agree"}");
                first = first ?? e1 ?? e2 ?? e3 ?? e4;
            }
            return first;
        }
    }
}
