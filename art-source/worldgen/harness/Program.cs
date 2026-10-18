// Standalone Mono harness for the world generator (fast iteration outside Unity).
//   build.sh noisestats
//   build.sh map <seed> <cx0> <cz0> <chunks> <step> <outPrefix>
//   build.sh perf <seed> [chunks]
//   build.sh probe <seed>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Wildmere.World;
using Wildmere.World.Gen;

static class Program
{
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "noisestats";
        switch (mode)
        {
            case "noisestats": NoiseStats(); return 0;
            case "map":
            {
                var gen = new TerrainGenerator(long.Parse(args[1]));
                int cx0 = int.Parse(args[2]), cz0 = int.Parse(args[3]), n = int.Parse(args[4]), step = int.Parse(args[5]);
                var sw = Stopwatch.StartNew();
                var r = WorldMap.Render(gen, cx0, cz0, n, step, Environment.ProcessorCount);
                WorldMap.WritePng(args[6] + "_biome.png", r.Width, r.Height, r.BiomeRgb);
                WorldMap.WritePng(args[6] + "_surface.png", r.Width, r.Height, r.SurfaceRgb);
                Console.WriteLine($"map {r.Width}x{r.Height} chunks={r.Chunks} wall={sw.Elapsed.TotalSeconds:F1}s ms/chunk(thread)={r.MsPerChunk:F2}");
                var sp = gen.FindSpawn();
                Console.WriteLine($"spawn {sp} biome={gen.SampleBiome(sp.x, sp.z).Biome}");
                return 0;
            }
            case "perf":
            {
                var gen = new TerrainGenerator(long.Parse(args[1]));
                int n = args.Length > 2 ? int.Parse(args[2]) : 64;
                for (int i = 0; i < 16; i++) gen.Generate(new Chunk(100 + i, 50));
                var sw = Stopwatch.StartNew();
                int side = (int)Math.Sqrt(n);
                for (int i = 0; i < side * side; i++) gen.Generate(new Chunk(i % side - side / 2, i / side - side / 2));
                Console.WriteLine($"perf {side * side} chunks, {sw.Elapsed.TotalMilliseconds / (side * side):F2} ms/chunk single thread");
                return 0;
            }
            case "probe": Probe(long.Parse(args[1])); return 0;
            case "checks": return Checks(long.Parse(args[1]));
            case "spots":
            {
                var g = new TerrainGenerator(long.Parse(args[1]));
                foreach (Biome b in new[] { Biome.Plains, Biome.TemperateForest, Biome.DarkForest, Biome.Desert, Biome.SnowyMountains, Biome.River, Biome.Beach, Biome.Ocean })
                    if (Wildmere.Tests.WorldGenChecks.FindBiomeChunk(g, b, out int cx, out int cz))
                    {
                        var c = g.SampleColumn(cx * 16 + 8, cz * 16 + 8);
                        Console.WriteLine($"{b,-16} chunk {cx},{cz} centre {cx * 16 + 8},{cz * 16 + 8} h={c.Height} water={c.Water}");
                    }
                return 0;
            }
            case "deep":
            {
                // block-level checks on many random 3x3 areas (rare-case hunting)
                var g = new TerrainGenerator(long.Parse(args[1]));
                int n = args.Length > 2 ? int.Parse(args[2]) : 60, fails = 0;
                var r = new SplitMix(4242);
                for (int i = 0; i < n; i++)
                {
                    int cx = r.Range(-200, 200), cz = r.Range(-200, 200);
                    var ar = new Wildmere.Tests.WorldGenChecks.Area(g, cx, cz);
                    string e = Wildmere.Tests.WorldGenChecks.TreeBlocks(ar, out _, out _) ?? Wildmere.Tests.WorldGenChecks.VegetationAndProps(ar, out _, out _)
                               ?? Wildmere.Tests.WorldGenChecks.SealedWater(ar) ?? Wildmere.Tests.WorldGenChecks.TreeDecisionsAgree(g, cx, cz, out _);
                    if (e != null) { fails++; Console.WriteLine($"FAIL area {cx},{cz} ({g.SampleColumn(cx * 16 + 8, cz * 16 + 8).Biome}): {e}"); }
                }
                Console.WriteLine($"deep: {n} areas, {fails} failures");
                return fails == 0 ? 0 : 1;
            }
            case "dump":
            {
                var g = new TerrainGenerator(long.Parse(args[1]));
                int x = int.Parse(args[2]), y = int.Parse(args[3]), z = int.Parse(args[4]);
                var ar = new Wildmere.Tests.WorldGenChecks.Area(g, x >> 4, z >> 4);
                var trees = new List<TerrainGenerator.Tree>();
                g.CollectTrees(x >> 4, z >> 4, trees);
                foreach (var t in trees) if (Math.Abs(t.X - x) <= 8 && Math.Abs(t.Z - z) <= 8) Console.WriteLine($"tree {t.Type} at {t.X},{t.Y},{t.Z} (col h={g.SampleColumn(t.X, t.Z).Height})");
                for (int yy = y + 6; yy >= y - 4; yy--)
                {
                    var sb = new System.Text.StringBuilder($"y={yy,3}: ");
                    for (int dx = -3; dx <= 3; dx++) sb.Append($"{ar.Get(x + dx, yy, z),3} ");
                    Console.WriteLine(sb);
                }
                return 0;
            }
            case "riverforest":
            {
                var g = new TerrainGenerator(long.Parse(args[1]));
                for (int r = 0; r < 1500; r += 8)
                    for (int k = 0; k < 64; k++)
                    {
                        int x = (int)(Math.Cos(k * 0.0982) * r), z = (int)(Math.Sin(k * 0.0982) * r);
                        var c = g.SampleColumn(x, z);
                        if (!c.River || c.Frozen) continue;
                        int forest = 0;
                        for (int q = 0; q < 8; q++) { var n = g.SampleColumn(x + (int)(Math.Cos(q * 0.785) * 24), z + (int)(Math.Sin(q * 0.785) * 24)); if (n.Biome == Biome.TemperateForest) forest++; }
                        if (forest >= 4) { Console.WriteLine($"river in forest at {x},{z} water={c.Water} h={c.Height}"); return 0; }
                    }
                return 1;
            }
            case "peak":
            {
                var g = new TerrainGenerator(long.Parse(args[1]));
                int x0 = int.Parse(args[2]), z0 = int.Parse(args[3]), x1 = int.Parse(args[4]), z1 = int.Parse(args[5]);
                int bx = 0, bz = 0, bh = -1;
                for (int z = z0; z <= z1; z += 4) for (int x = x0; x <= x1; x += 4) { var c = g.SampleColumn(x, z); if (c.Height > bh) { bh = c.Height; bx = x; bz = z; } }
                Console.WriteLine($"peak {bx},{bz} h={bh} {g.SampleColumn(bx, bz).Biome}");
                return 0;
            }
            case "col":
            {
                var g = new TerrainGenerator(long.Parse(args[1]));
                for (int i = 2; i + 1 < args.Length; i += 2)
                {
                    int x = int.Parse(args[i]), z = int.Parse(args[i + 1]);
                    var c = g.SampleColumn(x, z);
                    Console.WriteLine($"{x},{z}: h={c.Height} water={c.Water} biome={c.Biome} T={c.Temperature:F2} H={c.Humidity:F2} river={c.River} lake={c.Lake} ocean={c.Ocean} frozen={c.Frozen}");
                }
                return 0;
            }
        }
        Console.WriteLine("unknown mode");
        return 2;
    }

    static int Checks(long seed)
    {
        var g = new TerrainGenerator(seed);
        int fails = 0;
        void R(string name, string err) { Console.WriteLine($"{(err == null ? "PASS" : "FAIL")} {name} {err}"); if (err != null) fails++; }
        var sw = Stopwatch.StartNew();
        R("determinism", Wildmere.Tests.WorldGenChecks.Determinism(seed, Wildmere.Tests.WorldGenChecks.SpreadChunks(24)));
        R("parallel==serial", Wildmere.Tests.WorldGenChecks.ParallelEqualsSerial(seed, Wildmere.Tests.WorldGenChecks.SpreadChunks(64, 1), 8));
        var cov = Wildmere.Tests.WorldGenChecks.Coverage(g, 4096, 32);
        var sb = new System.Text.StringBuilder();
        foreach (var kv in cov) sb.Append($" {kv.Key}={kv.Value:P1}");
        Console.WriteLine("coverage" + sb);
        R("coverage", Wildmere.Tests.WorldGenChecks.CoverageVerdict(cov));
        int open = Wildmere.Tests.WorldGenChecks.CaveOpenings(g, Wildmere.Tests.WorldGenChecks.SpreadChunks(160, 7));
        R($"cave openings ({open})", open > 0 ? null : "none");
        R("ores", Wildmere.Tests.WorldGenChecks.OreRanges(g, Wildmere.Tests.WorldGenChecks.SpreadChunks(24, 11), out int coal, out int iron, out int gold));
        Console.WriteLine($"ores coal={coal} iron={iron} gold={gold}");
        var log = new System.Text.StringBuilder();
        R("biome areas", Wildmere.Tests.WorldGenChecks.BiomeAreaReport(g, log));
        Console.Write(log);
        R("spawn", Wildmere.Tests.WorldGenChecks.SpawnOk(g, out var sp));
        Console.WriteLine($"spawn {sp} {g.SampleBiome(sp.x, sp.z).Biome}");
        double ms = Wildmere.Tests.WorldGenChecks.MsPerChunk(g, 96);
        R($"speed {ms:F2} ms/chunk", ms <= 4.0 ? null : "too slow");
        Console.WriteLine($"total {sw.Elapsed.TotalSeconds:F1}s, {fails} failures");
        return fails == 0 ? 0 : 1;
    }

    static void Probe(long seed)
    {
        var gen = new TerrainGenerator(seed);
        var counts = new Dictionary<Biome, int>();
        int total = 0;
        for (int z = -4096; z < 4096; z += 32)
            for (int x = -4096; x < 4096; x += 32)
            {
                var b = gen.SampleColumn(x, z).Biome;
                counts.TryGetValue(b, out int c); counts[b] = c + 1; total++;
            }
        foreach (var kv in counts) Console.WriteLine($"biome {kv.Key,-16} {100.0 * kv.Value / total:F1}%");
    }

    static void NoiseStats()
    {
        var n = new Noise(12345);
        for (int oct = 1; oct <= 5; oct++)
        {
            double s = 0, s2 = 0; int cnt = 0;
            for (int i = 0; i < 400; i++)
                for (int j = 0; j < 400; j++)
                {
                    double v = oct == 1 ? n.Sample(i * 0.137, j * 0.137) : n.Fbm(i * 0.137, j * 0.137, oct);
                    s += v; s2 += v * v; cnt++;
                }
            double m = s / cnt;
            Console.WriteLine($"2D fbm oct={oct} mean={m:F4} std={Math.Sqrt(s2 / cnt - m * m):F4}");
        }
        for (int oct = 1; oct <= 3; oct++)
        {
            double s = 0, s2 = 0; int cnt = 0;
            for (int i = 0; i < 60; i++)
                for (int j = 0; j < 60; j++)
                    for (int k = 0; k < 60; k++)
                    {
                        double v = oct == 1 ? n.Sample(i * 0.137, j * 0.137, k * 0.137) : n.Fbm(i * 0.137, j * 0.137, k * 0.137, oct);
                        s += v; s2 += v * v; cnt++;
                    }
            double m = s / cnt;
            Console.WriteLine($"3D fbm oct={oct} mean={m:F4} std={Math.Sqrt(s2 / cnt - m * m):F4}");
        }
        for (int oct = 1; oct <= 5; oct++)
        {
            double s = 0, s2 = 0; int cnt = 0; var hist = new int[10];
            for (int i = 0; i < 400; i++)
                for (int j = 0; j < 400; j++)
                {
                    double v = n.Ridged(i * 0.137, j * 0.137, oct);
                    s += v; s2 += v * v; cnt++; hist[Math.Min(9, (int)(v * 10))]++;
                }
            double m = s / cnt;
            Console.WriteLine($"ridged oct={oct} mean={m:F4} std={Math.Sqrt(s2 / cnt - m * m):F4} hist={string.Join(",", hist)}");
        }
        var sw = Stopwatch.StartNew();
        double acc = 0;
        for (int i = 0; i < 2000000; i++) acc += n.Sample(i * 0.0137, i * 0.0071);
        Console.WriteLine($"2D sample: {sw.Elapsed.TotalMilliseconds * 1e6 / 2000000:F1} ns ({acc:F1})");
        sw.Restart();
        for (int i = 0; i < 2000000; i++) acc += n.Sample(i * 0.0137, i * 0.0071, i * 0.003);
        Console.WriteLine($"3D sample: {sw.Elapsed.TotalMilliseconds * 1e6 / 2000000:F1} ns ({acc:F1})");
    }
}
