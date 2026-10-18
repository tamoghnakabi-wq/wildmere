using System;
using System.IO;
using Wildmere.World.Gen;

namespace Wildmere.Tests
{
    /// <summary>
    /// Editor batch method: renders top-down world maps into art-source/worldgen/.
    ///   tools/unity.sh Wildmere.Tests.WorldGenMapExport.Run [-mapseed 1337] [-mapchunks 128] [-mapstep 2]
    /// </summary>
    public static class WorldGenMapExport
    {
        public static void Run()
        {
            long seed = Arg("-mapseed", 1337);
            int chunks = (int)Arg("-mapchunks", 128), step = (int)Arg("-mapstep", 2);
            string dir = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "art-source", "worldgen"));
            Directory.CreateDirectory(dir);
            var gen = new TerrainGenerator(seed);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = WorldMap.Render(gen, -chunks / 2, -chunks / 2, chunks, step, Math.Max(1, Environment.ProcessorCount - 1));
            string b = Path.Combine(dir, $"map_{seed}_biome.png"), s = Path.Combine(dir, $"map_{seed}_surface.png");
            WorldMap.WritePng(b, r.Width, r.Height, r.BiomeRgb);
            WorldMap.WritePng(s, r.Width, r.Height, r.SurfaceRgb);
            var sp = gen.FindSpawn();
            UnityEngine.Debug.Log($"WM_WORLDGEN_MAP {b} {r.Width}x{r.Height} ({chunks * 16} blocks) chunks={r.Chunks} " +
                                  $"wall={sw.Elapsed.TotalSeconds:F1}s ms/chunk(per thread, parallel)={r.MsPerChunk:F2} spawn={sp}");
        }

        static long Arg(string name, long def)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name && long.TryParse(a[i + 1], out long v)) return v;
            return def;
        }
    }
}
