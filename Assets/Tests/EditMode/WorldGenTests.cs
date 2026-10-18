using System.Text;
using NUnit.Framework;
using Wildmere.World;
using Wildmere.World.Gen;

namespace Wildmere.Tests
{
    /// <summary>World generator: determinism, thread safety, cross-border features, biome mix, caves, ores, speed.</summary>
    public class WorldGenTests
    {
        const long Seed = 1337;
        const long SelftestSeed = 20261017;

        [Test]
        public void Deterministic()
        {
            Assert.IsNull(WorldGenChecks.Determinism(Seed, WorldGenChecks.SpreadChunks(24)));
            Assert.IsNull(WorldGenChecks.Determinism(SelftestSeed, WorldGenChecks.SpreadChunks(12, 3)));
        }

        [Test]
        public void ParallelEqualsSerial()
        {
            Assert.IsNull(WorldGenChecks.ParallelEqualsSerial(Seed, WorldGenChecks.SpreadChunks(64, 1), 8));
        }

        [Test]
        public void TreesConsistentAcrossBorders()
        {
            var g = new TerrainGenerator(Seed);
            int totalShared = 0;
            foreach (var b in new[] { Biome.TemperateForest, Biome.DarkForest, Biome.SnowyMountains, Biome.Plains })
            {
                Assert.IsTrue(WorldGenChecks.FindBiomeChunk(g, b, out int cx, out int cz), $"no {b} chunk found");
                Assert.IsNull(WorldGenChecks.TreeDecisionsAgree(g, cx, cz, out int shared), b.ToString());
                totalShared += shared;
                var area = new WorldGenChecks.Area(g, cx, cz);
                Assert.IsNull(WorldGenChecks.TreeBlocks(area, out int logs, out int leaves), b.ToString());
                TestContext.WriteLine($"{b}: chunk {cx},{cz} trunks={logs} leaves={leaves} border-crossing trees={shared}");
                if (b == Biome.TemperateForest || b == Biome.DarkForest) Assert.Greater(logs, 3, $"{b} should be wooded");
            }
            Assert.Greater(totalShared, 0, "no tree crossed a chunk border; test would be vacuous");
        }

        [Test]
        public void BiomeCoverage()
        {
            foreach (long seed in new[] { Seed, SelftestSeed })
            {
                var cov = WorldGenChecks.Coverage(new TerrainGenerator(seed), 4096, 32);
                var sb = new StringBuilder($"seed {seed}:");
                foreach (var kv in cov) sb.Append($" {kv.Key}={kv.Value:P1}");
                TestContext.WriteLine(sb.ToString());
                Assert.IsNull(WorldGenChecks.CoverageVerdict(cov), $"seed {seed}");
            }
        }

        [Test]
        public void CaveOpeningsExist()
        {
            var g = new TerrainGenerator(Seed);
            int open = WorldGenChecks.CaveOpenings(g, WorldGenChecks.SpreadChunks(160, 7));
            TestContext.WriteLine($"cave-opening columns in 160 chunks: {open}");
            Assert.Greater(open, 0);
        }

        [Test]
        public void OreVeinsWithinRanges()
        {
            var g = new TerrainGenerator(Seed);
            Assert.IsNull(WorldGenChecks.OreRanges(g, WorldGenChecks.SpreadChunks(24, 11), out int coal, out int iron, out int gold));
            TestContext.WriteLine($"ores in 24 chunks: coal={coal} iron={iron} gold={gold}");
        }

        [Test]
        public void WaterSealedVegetationAndPropsGrounded()
        {
            var g = new TerrainGenerator(Seed);
            var log = new StringBuilder();
            string err = WorldGenChecks.BiomeAreaReport(g, log);
            TestContext.WriteLine(log.ToString());
            Assert.IsNull(err);
        }

        [Test]
        public void SpawnIsDryAndOpen()
        {
            foreach (long seed in new[] { Seed, SelftestSeed })
            {
                var g = new TerrainGenerator(seed);
                Assert.IsNull(WorldGenChecks.SpawnOk(g, out var sp), $"seed {seed}");
                TestContext.WriteLine($"seed {seed}: spawn {sp} biome {g.SampleBiome(sp.x, sp.z).Biome}");
            }
        }

        [Test]
        public void GenerationSpeed()
        {
            double ms = WorldGenChecks.MsPerChunk(new TerrainGenerator(Seed), 96);
            TestContext.WriteLine($"WM_WORLDGEN_PERF {ms:F2} ms per 16x256x16 column (single thread, editor Mono)");
            Assert.LessOrEqual(ms, 4.0);
        }
    }
}
