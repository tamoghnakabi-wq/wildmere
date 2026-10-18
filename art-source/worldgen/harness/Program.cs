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
        }
        Console.WriteLine("unknown mode");
        return 2;
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
