using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Wildmere.World.Gen
{
    /// <summary>
    /// Wildmere's world generator: climate-driven biomes (temperate forest, plains, dark forest, desert, snowy
    /// mountains, ocean, beach, river) with smoothly blended terrain shapes and tints, meandering rivers at sea
    /// level, alpine/forest lakes with their own water level, cheese + spaghetti caves, ore veins, trees, plants
    /// and decoration props.
    ///
    /// Thread-safety / determinism: every decision is a pure function of (seed, world coordinates). Expensive
    /// low-frequency fields are sampled on a world-aligned 4-block lattice and bilinearly interpolated; the
    /// per-chunk caches live in [ThreadStatic] scratch buffers, the only shared mutable state is the lake cache
    /// (a ConcurrentDictionary of pure values). Features that cross column borders (trees, ore veins) are
    /// evaluated from every column they touch, from world coordinates.
    ///
    /// Partial class files: Fields (lattice + per-column terrain), Surface (block layers), Caves, Ores, Trees,
    /// Decor (plants + props), Lakes.
    /// </summary>
    public sealed partial class TerrainGenerator : IWorldGenerator
    {
        public const int SeaLevel = 62;
        public long Seed { get; }

        // Lattice (2D, low frequency) noises.
        readonly Noise nWarpX, nWarpZ, nTemp, nHum, nCont, nEro, nRidge, nMassif, nRWarpX, nRWarpZ, nRiver,
            nHill, nMesa, nDune, nDuneMask, nCliff, nCliffAmp, nEnt, nVar;
        // Per-column noises.
        readonly Noise nDetail, nDetail2, nDither, nSnow, nLakeShape, nPatch, nFlower, nFlowerColor;
        // Caves.
        readonly Noise nCaveA, nCaveB, nCheese;

        readonly ConcurrentDictionary<long, Lake> mLakes = new ConcurrentDictionary<long, Lake>();

        public TerrainGenerator(long seed)
        {
            Seed = seed;
            int k = 0;
            Noise N() => new Noise(unchecked(seed * 0x5DEECE66DL + 0x2545F491L * ++k));
            nWarpX = N(); nWarpZ = N(); nTemp = N(); nHum = N(); nCont = N(); nEro = N(); nRidge = N(); nMassif = N();
            nRWarpX = N(); nRWarpZ = N(); nRiver = N(); nHill = N(); nMesa = N(); nDune = N(); nDuneMask = N();
            nCliff = N(); nCliffAmp = N(); nEnt = N(); nVar = N();
            nDetail = N(); nDetail2 = N(); nDither = N(); nSnow = N(); nLakeShape = N(); nPatch = N(); nFlower = N();
            nFlowerColor = N();
            nCaveA = N(); nCaveB = N(); nCheese = N();
        }

        // ------------------------------------------------------------------ scratch (per worker thread)

        /// <summary>Column cache margin around the chunk (covers tree candidates + their slope neighbours).</summary>
        internal const int CP = 10, CW = 16 + 2 * CP;
        internal const int LatN = 11;

        sealed class Scratch
        {
            public readonly double[] Lat = new double[LatN * LatN * NF];
            public int LatX0, LatZ0;                       // lattice index of Lat[0]
            public readonly Col[] Cols = new Col[CW * CW];
            public readonly int[] ColStamp = new int[CW * CW];
            public int Stamp;
            public int ColX0, ColZ0;                       // world coords of Cols[0]
            public readonly List<Lake> Lakes = new List<Lake>(8);
            public readonly List<Tree> Trees = new List<Tree>(96);
            public readonly bool[] Reserved = new bool[256];  // props/plants occupancy per inner column
            public readonly byte[] Slope = new byte[256];
            public readonly int[] NWater = new int[256];
            public readonly int[] Cap = new int[256];
            // caves
            public readonly double[] CaveA = new double[5 * 5 * 66], CaveB = new double[5 * 5 * 66], CaveC = new double[5 * 5 * 66];
            public readonly double[] ColA = new double[66], ColB = new double[66], ColC = new double[66];
            // direct (single cell) lattice
            public readonly double[] DLat = new double[4 * NF];
            public int DLx = int.MinValue, DLz = int.MinValue;
            public long DSeed;
            public readonly List<Lake> DLakes = new List<Lake>(8);
        }

        [ThreadStatic] static Scratch tScratch;
        static Scratch S => tScratch ?? (tScratch = new Scratch());

        // ------------------------------------------------------------------ IWorldGenerator

        public void Generate(Chunk c)
        {
            var s = S;
            int bx = c.CX * 16, bz = c.CZ * 16;
            Array.Clear(c.Blocks, 0, c.Blocks.Length);
            c.Props.Clear();
            Array.Clear(s.Reserved, 0, 256);

            PrepareRegion(s, bx, bz);

            // Inner columns + one ring (slope / wet-neighbour tests).
            for (int z = -1; z <= 16; z++)
                for (int x = -1; x <= 16; x++)
                    GetCol(s, bx + x, bz + z);

            ComputeSurfaceContext(s, bx, bz);
            FillColumns(c, s, bx, bz);
            CarveCaves(c, s, bx, bz);
            PlaceOres(c, bx, bz);
            PlaceTrees(c, s, bx, bz);
            PlaceProps(c, s, bx, bz);
            PlacePlants(c, s, bx, bz);
            c.RecomputeHeights();
        }

        public BiomeSample SampleBiome(int x, int z)
        {
            var col = SampleColumnInternal(x, z, true);
            return new BiomeSample
            {
                Biome = (Biome)col.Biome,
                Temperature = col.T,
                Humidity = col.Hum,
                Forest = (float)Math.Min(1.0, col.WFo * 0.7 + col.WDk + col.WPl * 0.05 + col.WSn * 0.35),
                Darkness = col.WDk,
                Snowy = col.WSn > 0.5f || col.H >= col.Snow - 2,
                Height = Math.Max(col.H, col.Water),
            };
        }

        /// <summary>Terrain facts for one column without generating a chunk (map rendering, tests, tools).</summary>
        public struct ColumnSample
        {
            public int Height;      // top solid terrain block (before caves, trees and plants)
            public int Water;       // water surface y, or -1 when the column is dry
            public Biome Biome;     // dominant biome (dithered at borders)
            public bool River, Lake, Ocean, Beach, Frozen;
            public float Temperature, Humidity;
        }

        public ColumnSample SampleColumn(int x, int z)
        {
            var c = SampleColumnInternal(x, z, true);
            return new ColumnSample
            {
                Height = c.H,
                Water = c.Water > c.H ? c.Water : -1,
                Biome = (Biome)c.Biome,
                River = (c.Flags & FRiver) != 0,
                Lake = (c.Flags & FLake) != 0,
                Ocean = (c.Flags & FOcean) != 0,
                Beach = (c.Flags & FBeach) != 0,
                Frozen = (c.Flags & FFrozen) != 0,
                Temperature = c.T,
                Humidity = c.Hum,
            };
        }

        public Vector3Int FindSpawn()
        {
            // Spiral outwards; prefer open plains near a forest edge, then any plains/forest, then any dry land.
            for (int pass = 0; pass < 3; pass++)
            {
                for (int r = 0; r <= 1600; r += 6)
                {
                    int steps = Math.Max(1, (int)(r * 6.2832 / 6));
                    for (int a = 0; a < steps; a++)
                    {
                        double ang = a * 6.2832 / steps;
                        int x = (int)Math.Round(Math.Cos(ang) * r), z = (int)Math.Round(Math.Sin(ang) * r);
                        if (SpawnOk(x, z, pass, out int y)) return new Vector3Int(x, y, z);
                    }
                }
            }
            var c0 = SampleColumnInternal(0, 0, true);
            return new Vector3Int(0, Math.Max(c0.H, c0.Water) + 1, 0);
        }

        bool SpawnOk(int x, int z, int pass, out int y)
        {
            y = 0;
            var c = SampleColumnInternal(x, z, true);
            if (c.Water > c.H || c.H < SeaLevel + 2 || c.H > 110 || c.Ent > 0) return false;
            var biome = (Biome)c.Biome;
            if (pass == 0)
            {
                if (biome != Biome.Plains || c.WFo < 0.12f) return false;
            }
            else if (pass == 1)
            {
                if (biome != Biome.Plains && biome != Biome.TemperateForest) return false;
            }
            else if (biome == Biome.Beach || biome == Biome.Ocean || biome == Biome.River) return false;

            // Flat and dry 7x7 patch.
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = SampleColumnInternal(x + dx, z + dz, true);
                    if (n.Water > n.H || Math.Abs(n.H - c.H) > 1) return false;
                }
            // Not inside or under a tree.
            if (TreeNear(x, z, 3)) return false;
            y = c.H + 1;
            return true;
        }
    }
}
