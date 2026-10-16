using System;
using UnityEngine;

namespace Wildmere.World.Gen
{
    /// <summary>
    /// Baseline generator written by the lead for Phase 1 (the World Generation agent owns and replaces
    /// this file). Rolling hills, sea, beaches, oak trees, plants, simple caves and ores.
    /// </summary>
    public sealed class TerrainGenerator : IWorldGenerator
    {
        public const int SeaLevel = 62;
        public long Seed { get; }

        readonly Noise mHeight, mDetail, mMount, mTemp, mHumid, mCaveA, mCaveB, mPlants;

        public TerrainGenerator(long seed)
        {
            Seed = seed;
            mHeight = new Noise(seed * 31 + 1);
            mDetail = new Noise(seed * 31 + 2);
            mMount = new Noise(seed * 31 + 3);
            mTemp = new Noise(seed * 31 + 4);
            mHumid = new Noise(seed * 31 + 5);
            mCaveA = new Noise(seed * 31 + 6);
            mCaveB = new Noise(seed * 31 + 7);
            mPlants = new Noise(seed * 31 + 8);
        }

        public int Height(int x, int z)
        {
            double h = mHeight.Fbm(x / 220.0, z / 220.0, 4) * 18.0 + mDetail.Fbm(x / 48.0, z / 48.0, 3) * 4.0;
            double m = mMount.Ridged(x / 400.0, z / 400.0, 4);
            double mask = Math.Max(0, mMount.Sample(x / 900.0 + 40, z / 900.0 - 7) * 1.6);
            h += m * m * 70.0 * Math.Min(1.0, mask);
            return (int)Math.Round(SeaLevel + 6 + h);
        }

        public BiomeSample SampleBiome(int x, int z)
        {
            float t = (float)(mTemp.Fbm(x / 700.0, z / 700.0, 2) * 0.5 + 0.5);
            float hu = (float)(mHumid.Fbm(x / 600.0, z / 600.0, 2) * 0.5 + 0.5);
            int h = Height(x, z);
            var s = new BiomeSample { Temperature = t, Humidity = hu, Height = h };
            if (h <= SeaLevel - 3) s.Biome = Biome.Ocean;
            else if (h <= SeaLevel + 1) s.Biome = Biome.Beach;
            else if (h > 120) s.Biome = Biome.SnowyMountains;
            else s.Biome = hu > 0.55f ? Biome.TemperateForest : Biome.Plains;
            s.Forest = s.Biome == Biome.TemperateForest ? 0.7f : 0.06f;
            s.Snowy = s.Biome == Biome.SnowyMountains;
            return s;
        }

        public Vector3Int FindSpawn()
        {
            for (int r = 0; r < 400; r += 8)
                for (int a = 0; a < 16; a++)
                {
                    int x = (int)(Math.Cos(a * 0.3927) * r), z = (int)(Math.Sin(a * 0.3927) * r);
                    int h = Height(x, z);
                    if (h > SeaLevel + 2 && h < 100) return new Vector3Int(x, h + 1, z);
                }
            return new Vector3Int(0, Height(0, 0) + 1, 0);
        }

        public void Generate(Chunk c)
        {
            var blocks = c.Blocks;
            int bx = c.CX * 16, bz = c.CZ * 16;
            var heights = new int[256];

            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wx = bx + x, wz = bz + z;
                    int h = Math.Min(Height(wx, wz), Chunk.SY - 2);
                    heights[x + z * 16] = h;
                    var bs = SampleBiome(wx, wz);
                    c.Biome[x + z * 16] = (byte)bs.Biome;
                    c.GrassTint[x + z * 16] = Tint(bs, false);
                    c.FoliageTint[x + z * 16] = Tint(bs, true);

                    bool beach = h <= SeaLevel + 1;
                    bool snowy = h > 118;
                    int dirtDepth = 3 + (int)(SplitMix.Hash01(Seed, wx, wz, 11) * 2);
                    for (int y = 0; y <= Math.Max(h, SeaLevel); y++)
                    {
                        byte b;
                        if (y == 0) b = B.Bedrock;
                        else if (y > h) b = y <= SeaLevel ? B.Water : B.Air;
                        else if (y == h) b = beach ? B.Sand : snowy ? B.Snow : h > 105 ? B.Stone : B.Grass;
                        else if (y > h - dirtDepth) b = beach ? B.Sand : h > 105 ? B.Stone : B.Dirt;
                        else b = B.Stone;
                        blocks[x + (z << 4) + (y << 8)] = b;
                    }
                }

            Caves(c, heights);
            Ores(c);

            // Trees and plants (trees may hang into this column from neighbours within 3 blocks).
            for (int tz = bz - 3; tz < bz + 19; tz++)
                for (int tx = bx - 3; tx < bx + 19; tx++)
                {
                    var bs = SampleBiome(tx, tz);
                    double chance = bs.Forest * 0.045;
                    if (SplitMix.Hash01(Seed, tx, tz, 21) >= chance) continue;
                    int h = Height(tx, tz);
                    if (h <= SeaLevel + 1 || h > 110) continue;
                    if (CaveAt(tx, h, tz)) continue;
                    int trunk = 4 + (int)(SplitMix.Hash01(Seed, tx, tz, 22) * 3);
                    Tree(c, tx - bx, h + 1, tz - bz, trunk);
                }

            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int h = heights[x + z * 16];
                    if (h >= Chunk.SY - 2) continue;
                    int idx = Chunk.Index(x, h, z);
                    if (blocks[idx] != B.Grass || blocks[idx + 256] != B.Air) continue;
                    double r = SplitMix.Hash01(Seed, bx + x, bz + z, 31);
                    double meadow = mPlants.Sample((bx + x) / 30.0, (bz + z) / 30.0) * 0.5 + 0.5;
                    if (r < 0.10 + meadow * 0.18) blocks[idx + 256] = B.TallGrass;
                    else if (r < 0.10 + meadow * 0.18 + meadow * meadow * 0.05)
                        blocks[idx + 256] = (byte)(B.FlowerRed + (int)(SplitMix.Hash01(Seed, bx + x, bz + z, 32) * 4));
                }

            c.RecomputeHeights();
        }

        static Color32 Tint(BiomeSample s, bool foliage)
        {
            Color dry = new Color(0.62f, 0.68f, 0.32f), lush = new Color(0.34f, 0.60f, 0.22f), cold = new Color(0.40f, 0.55f, 0.42f);
            Color c = Color.Lerp(dry, lush, s.Humidity);
            c = Color.Lerp(cold, c, Mathf.Clamp01(s.Temperature * 2f));
            if (foliage) c *= 0.88f;
            return (Color32)c.gamma;
        }

        bool CaveAt(int x, int y, int z)
        {
            if (y < 6) return false;
            double a = mCaveA.Fbm(x / 42.0, y / 28.0, z / 42.0, 2);
            double b = mCaveB.Fbm(x / 42.0, y / 28.0, z / 42.0, 2);
            return a * a + b * b < 0.0055;
        }

        void Caves(Chunk c, int[] heights)
        {
            int bx = c.CX * 16, bz = c.CZ * 16;
            // Sample the expensive 3D field on a 4x4x4 lattice and interpolate.
            const int S = 4;
            int ny = Chunk.SY / S + 1;
            var field = new double[5 * ny * 5];
            for (int iz = 0; iz < 5; iz++)
                for (int iy = 0; iy < ny; iy++)
                    for (int ix = 0; ix < 5; ix++)
                    {
                        double x = bx + ix * S, y = iy * S, z = bz + iz * S;
                        double a = mCaveA.Fbm(x / 42.0, y / 28.0, z / 42.0, 2);
                        double b = mCaveB.Fbm(x / 42.0, y / 28.0, z / 42.0, 2);
                        field[ix + iy * 5 + iz * 5 * ny] = a * a + b * b;
                    }

            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int h = heights[x + z * 16];
                    int top = Math.Min(h, 200);
                    for (int y = 6; y <= top; y++)
                    {
                        double fx = x / (double)S, fy = y / (double)S, fz = z / (double)S;
                        int ix = (int)fx, iy = (int)fy, iz = (int)fz;
                        double tx = fx - ix, ty = fy - iy, tz = fz - iz;
                        double v = Tri(field, ix, iy, iz, tx, ty, tz, ny);
                        // Keep a skin near the surface below sea level so oceans don't drain into caves.
                        if (v < 0.0055 && !(h <= SeaLevel && y > h - 4))
                        {
                            int idx = x + (z << 4) + (y << 8);
                            byte old = c.Blocks[idx];
                            if (old != B.Water && old != B.Bedrock) c.Blocks[idx] = y < 11 ? B.Water : B.Air;
                        }
                    }
                }
        }

        static double Tri(double[] f, int ix, int iy, int iz, double tx, double ty, double tz, int ny)
        {
            double V(int x, int y, int z) => f[x + y * 5 + z * 5 * ny];
            double c00 = V(ix, iy, iz) * (1 - tx) + V(ix + 1, iy, iz) * tx;
            double c10 = V(ix, iy + 1, iz) * (1 - tx) + V(ix + 1, iy + 1, iz) * tx;
            double c01 = V(ix, iy, iz + 1) * (1 - tx) + V(ix + 1, iy, iz + 1) * tx;
            double c11 = V(ix, iy + 1, iz + 1) * (1 - tx) + V(ix + 1, iy + 1, iz + 1) * tx;
            double c0 = c00 * (1 - ty) + c10 * ty, c1 = c01 * (1 - ty) + c11 * ty;
            return c0 * (1 - tz) + c1 * tz;
        }

        void Ores(Chunk c)
        {
            var rng = new SplitMix(Seed ^ Chunk.Key(c.CX, c.CZ) * 7919);
            Vein(c, ref rng, B.CoalOre, 18, 8, 5, 130);
            Vein(c, ref rng, B.IronOre, 10, 6, 5, 70);
            Vein(c, ref rng, B.GoldOre, 3, 6, 5, 32);
        }

        static void Vein(Chunk c, ref SplitMix rng, byte ore, int count, int size, int minY, int maxY)
        {
            for (int i = 0; i < count; i++)
            {
                int x = rng.Range(0, 16), y = rng.Range(minY, maxY), z = rng.Range(0, 16);
                for (int k = 0; k < size; k++)
                {
                    if (x >= 0 && x < 16 && z >= 0 && z < 16 && y > 0 && y < Chunk.SY)
                    {
                        int idx = Chunk.Index(x, y, z);
                        if (c.Blocks[idx] == B.Stone) c.Blocks[idx] = ore;
                    }
                    switch (rng.Range(0, 6))
                    {
                        case 0: x++; break; case 1: x--; break; case 2: y++; break;
                        case 3: y--; break; case 4: z++; break; default: z--; break;
                    }
                }
            }
        }

        static void Set(Chunk c, int x, int y, int z, byte b, bool overwrite)
        {
            if (x < 0 || x > 15 || z < 0 || z > 15 || y < 0 || y >= Chunk.SY) return;
            int idx = Chunk.Index(x, y, z);
            byte old = c.Blocks[idx];
            if (overwrite || old == B.Air || Blocks.IsPlant(old)) c.Blocks[idx] = b;
        }

        static void Tree(Chunk c, int x, int y, int z, int trunk)
        {
            for (int i = 0; i < trunk; i++) Set(c, x, y + i, z, B.LogOak, true);
            int top = y + trunk;
            for (int dy = -2; dy <= 1; dy++)
            {
                int r = dy <= -1 ? 2 : 1;
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Math.Abs(dx) == r && Math.Abs(dz) == r && (dy == 1 || ((dx + dz + y) & 1) == 0)) continue;
                        if (dx == 0 && dz == 0 && dy < 1) continue;
                        Set(c, x + dx, top + dy, z + dz, B.LeavesOak, false);
                    }
            }
            Set(c, x, top + 1, z, B.LeavesOak, false);
        }
    }
}
