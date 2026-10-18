using System;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        internal const double SigPerlin = 0.184;   // measured std of a single 2D Perlin sample

        /// <summary>Surface layers of a column (top block, filler, deeper filler) from its context.</summary>
        void Layers(int x, int z, in Col c, int slope, int nWater, out byte top, out byte sub, out int subD, out byte deep, out int deepD)
        {
            ulong hh = SplitMix.Hash(Seed, x, z, 41);
            double h01 = (hh & 0xFFFF) / 65536.0;
            double patch = nPatch.Sample(x / 9.0, z / 9.0) / (SigPerlin * 2.2);
            subD = 3 + (int)((hh >> 16) % 3);
            deep = B.Stone; deepD = 0;
            int H = c.H;
            var clim = (Biome)c.Climate;

            if (c.Water > H)
            {
                int depth = c.Water - H;
                if ((c.Flags & FOcean) != 0)
                {
                    if (depth > 14) top = patch > 0.45 ? B.Clay : patch < -0.35 ? B.Sand : B.Gravel;
                    else top = patch > 0.62 ? B.Gravel : patch < -0.72 ? B.Clay : B.Sand;
                    if ((c.Flags & FFrozen) != 0 && top == B.Sand && patch > 0.1) top = B.Gravel;
                }
                else if ((c.Flags & FRiver) != 0)
                    top = patch < -0.35 ? B.Gravel : patch > 0.42 ? B.Clay : B.Sand;
                else if (c.WSn > 0.4f || H > 125)
                    top = patch > -0.2 ? B.Gravel : B.Stone;
                else if (clim == Biome.Desert)
                    top = B.Sand;
                else
                    top = patch > 0.35 ? B.Clay : patch < -0.45 ? B.Gravel : depth > 3 ? B.Dirt : B.Sand;
                sub = top == B.Dirt ? B.Dirt : top;
                if (top == B.Clay) subD = 2;
                if (top == B.Sand) { deep = B.Sandstone; deepD = 2; }
                return;
            }

            bool steep = slope >= 3;
            double snowLine = c.Snow;

            // Altitude: rock and snow (any climate), snowy mountain floors.
            if (H >= snowLine || clim == Biome.SnowyMountains)
            {
                if (steep || (slope == 2 && H >= snowLine && patch > 0.15)) { top = B.Stone; sub = B.Stone; return; }
                if (H >= snowLine)
                {
                    top = B.Snow; sub = slope == 0 || h01 < 0.4 ? B.Snow : B.Stone; subD = 1 + (slope == 0 ? 1 : 0);
                    return;
                }
                if (nWater > 0 && H <= nWater + 1) { top = B.Gravel; sub = B.Gravel; subD = 2; return; }
                double grassLine = snowLine - 46 + patch * 10;
                if (H >= grassLine || patch > 0.55) { top = B.SnowyGrass; sub = B.Dirt; return; }
                if (slope == 2 && patch < -0.5) { top = B.Gravel; sub = B.Gravel; subD = 2; return; }
                top = B.Grass; sub = B.Dirt; return;
            }

            if ((c.Flags & FBeach) != 0)
            {
                if (steep) { top = B.Stone; sub = B.Stone; return; }
                bool gravel = patch > 0.62 || (c.T < 0.34f && patch > 0.05);
                top = gravel ? B.Gravel : B.Sand; sub = top;
                if (!gravel) { deep = B.Sandstone; deepD = 2; }
                return;
            }

            // Shores of rivers and lakes: sand / gravel / clay at the water line, gentle grassy banks above.
            if (nWater > 0 && H <= nWater + 1 && clim != Biome.Desert)
            {
                if (H <= nWater || patch < -0.1)
                {
                    top = patch > 0.5 ? B.Gravel : patch < -0.75 ? B.Clay : B.Sand; sub = top;
                    if (top == B.Sand) { deep = B.Sandstone; deepD = 1; }
                    return;
                }
            }

            switch (clim)
            {
                case Biome.Desert:
                    if (c.Mesa > 0.15f && slope >= 2) { top = B.Sandstone; sub = B.Sandstone; return; }
                    if (steep) { top = B.Sandstone; sub = B.Sandstone; return; }
                    top = B.Sand; sub = B.Sand; deep = B.Sandstone; deepD = 5 + (int)((hh >> 24) & 3);
                    return;
                case Biome.DarkForest:
                    if (steep) { top = B.Stone; sub = B.Stone; return; }
                    top = patch < 0.35 ? B.Podzol : B.Grass; sub = B.Dirt;
                    return;
                default:
                    if (steep || (H > 118 && slope >= 2)) { top = B.Stone; sub = B.Stone; return; }
                    top = B.Grass; sub = B.Dirt;
                    return;
            }
        }

        /// <summary>Slope and the highest neighbouring water level for any cached column (shared by fill + trees).</summary>
        void Context(Scratch s, int x, int z, out int slope, out int nWater)
        {
            Col c = GetCol(s, x, z);
            slope = 0; nWater = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                Col n = GetCol(s, nx, nz);
                int d = Math.Abs(n.H - c.H); if (d > slope) slope = d;
                if (n.Water > n.H && n.Water > nWater) nWater = n.Water;
            }
        }

        /// <summary>The top block a cached column will get (used by tree/prop decisions from any chunk).</summary>
        byte TopBlock(Scratch s, int x, int z)
        {
            Col c = GetCol(s, x, z);
            Context(s, x, z, out int slope, out int nWater);
            Layers(x, z, in c, slope, nWater, out byte top, out _, out _, out _, out _);
            return top;
        }

        // ------------------------------------------------------------------ tints (sRGB; the shader blends between columns)

        // Plains, Forest, DarkForest, Desert, Snowy
        static readonly double[] kGrass = { 153, 218, 82, 116, 194, 70, 84, 150, 56, 214, 206, 118, 128, 178, 124 };
        static readonly double[] kFoliage = { 100, 160, 52, 78, 140, 45, 66, 118, 44, 150, 160, 74, 70, 108, 80 };

        static UnityEngine.Color32 Tint(in Col c, double[] t, bool foliage)
        {
            double r = c.WPl * t[0] + c.WFo * t[3] + c.WDk * t[6] + c.WDe * t[9] + c.WSn * t[12];
            double g = c.WPl * t[1] + c.WFo * t[4] + c.WDk * t[7] + c.WDe * t[10] + c.WSn * t[13];
            double b = c.WPl * t[2] + c.WFo * t[5] + c.WDk * t[8] + c.WDe * t[11] + c.WSn * t[14];
            // altitude cools the colour (mountain foothills), slow noise adds natural variety
            double alt = SS(105, 165, c.H) * 0.6;
            r += (t[12] - r) * alt; g += (t[13] - g) * alt; b += (t[14] - b) * alt;
            double v = 1 + 0.07 * c.Var;
            double warm = 0.04 * c.Var * (foliage ? 0.5 : 1);
            r *= v + warm; g *= v; b *= v - warm;
            return new UnityEngine.Color32(B8(r), B8(g), B8(b), 255);
        }

        static byte B8(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5);

        // ------------------------------------------------------------------ block fill

        void FillColumns(Chunk ch, Scratch s, int bx, int bz)
        {
            var blocks = ch.Blocks;
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wx = bx + x, wz = bz + z, i = x + z * 16;
                    Col c = GetCol(s, wx, wz);
                    Layers(wx, wz, in c, s.Slope[i], s.NWater[i], out byte top, out byte sub, out int subD, out byte deep, out int deepD);
                    ch.Biome[i] = c.Biome;
                    ch.GrassTint[i] = Tint(in c, kGrass, false);
                    ch.FoliageTint[i] = Tint(in c, kFoliage, true);

                    int col = x + (z << 4);
                    int H = c.H;
                    blocks[col] = B.Bedrock;
                    for (int y = 1; y <= 3 && y <= H; y++)
                        blocks[col + (y << 8)] = Hash3(Seed, wx, y, wz, 3) < (y == 1 ? 0.72 : y == 2 ? 0.42 : 0.16) ? B.Bedrock : B.Stone;
                    for (int y = 4; y <= H; y++) blocks[col + (y << 8)] = B.Stone;

                    // Mesa strata (horizontal bands, world-aligned) in the raised desert rock.
                    if (c.Mesa > 0.05f && H > c.Base + 4)
                    {
                        int tilt = (int)Math.Round(c.Var * 2.5);
                        for (int y = Math.Max(4, (int)c.Base - 2); y < H; y++)
                        {
                            double band = SplitMix.Hash01(Seed, y + tilt, 0, 777);
                            blocks[col + (y << 8)] = band < 0.12 ? B.Sand : band < 0.20 ? B.Clay : band < 0.25 ? B.Stone : B.Sandstone;
                        }
                    }

                    int yy = H;
                    blocks[col + (yy << 8)] = top; yy--;
                    for (int k = 0; k < subD && yy > 3; k++, yy--) blocks[col + (yy << 8)] = sub;
                    for (int k = 0; k < deepD && yy > 3; k++, yy--) blocks[col + (yy << 8)] = deep;

                    if (c.Water > H)
                    {
                        for (int y = H + 1; y <= c.Water; y++) blocks[col + (y << 8)] = B.Water;
                        if ((c.Flags & FFrozen) != 0) blocks[col + (c.Water << 8)] = B.Ice;
                    }
                }
        }
    }
}
