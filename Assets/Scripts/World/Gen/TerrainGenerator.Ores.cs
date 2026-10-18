using System;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        public const int CoalMinY = 5, CoalMaxY = 140, IronMinY = 5, IronMaxY = 72, GoldMinY = 5, GoldMaxY = 34;

        /// <summary>
        /// Ore veins and gravel / dirt / clay pockets. Each feature is seeded by the chunk it starts in and may
        /// reach up to 8 blocks into neighbours, so every chunk replays its 3x3 neighbourhood (world-consistent).
        /// </summary>
        void PlaceOres(Chunk c, int bx, int bz)
        {
            for (int nz = c.CZ - 1; nz <= c.CZ + 1; nz++)
                for (int nx = c.CX - 1; nx <= c.CX + 1; nx++)
                {
                    var rng = new SplitMix(unchecked(Seed * 0x632BE59BD9B4E019L) ^ unchecked(Chunk.Key(nx, nz) * (long)0x9E3779B97F4A7C15UL));
                    int ox = nx * 16, oz = nz * 16;
                    Blobs(c, bx, bz, ref rng, ox, oz, B.Gravel, 3, 2.0, 3.4, 8, 110, false);
                    Blobs(c, bx, bz, ref rng, ox, oz, B.Dirt, 2, 2.0, 3.2, 20, 100, false);
                    Blobs(c, bx, bz, ref rng, ox, oz, B.Clay, 1, 1.6, 2.8, 40, 66, true);
                    // Coal: common, fattest around y 40..100.
                    for (int i = 0; i < 20; i++)
                    {
                        int y = rng.Range(CoalMinY, CoalMaxY + 1);
                        if (y > 100 && rng.NextDouble() < 0.5) y = rng.Range(CoalMinY, 100);
                        Vein(c, bx, bz, ref rng, ox, oz, y, B.CoalOre, 7 + rng.Range(0, 7), CoalMinY, CoalMaxY);
                    }
                    for (int i = 0; i < 11; i++)
                    {
                        // Iron: triangular distribution peaking around y 30.
                        int y = (rng.Range(IronMinY, IronMaxY + 1) + rng.Range(IronMinY, 56)) / 2;
                        Vein(c, bx, bz, ref rng, ox, oz, y, B.IronOre, 4 + rng.Range(0, 6), IronMinY, IronMaxY);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        int y = rng.Range(GoldMinY, GoldMaxY + 1);
                        Vein(c, bx, bz, ref rng, ox, oz, y, B.GoldOre, 3 + rng.Range(0, 5), GoldMinY, GoldMaxY);
                    }
                }
        }

        static void Vein(Chunk c, int bx, int bz, ref SplitMix rng, int ox, int oz, int y, byte ore, int size, int minY, int maxY)
        {
            int x = ox + rng.Range(0, 16), z = oz + rng.Range(0, 16);
            for (int k = 0; k < size; k++)
            {
                int lx = x - bx, lz = z - bz;
                if ((uint)lx < 16 && (uint)lz < 16 && y >= minY && y <= maxY)
                {
                    int idx = lx + (lz << 4) + (y << 8);
                    if (c.Blocks[idx] == B.Stone) c.Blocks[idx] = ore;
                }
                switch (rng.Range(0, 6))
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: y++; break;
                    case 3: y--; break;
                    case 4: z++; break;
                    default: z--; break;
                }
            }
        }

        static void Blobs(Chunk c, int bx, int bz, ref SplitMix rng, int ox, int oz, byte b, int count, double rMin, double rMax,
            int minY, int maxY, bool soft)
        {
            for (int i = 0; i < count; i++)
            {
                int cx = ox + rng.Range(0, 16), cy = rng.Range(minY, maxY + 1), cz = oz + rng.Range(0, 16);
                double r = rMin + rng.NextDouble() * (rMax - rMin), ry = r * (0.6 + 0.3 * rng.NextDouble());
                int ri = (int)Math.Ceiling(r), ryi = (int)Math.Ceiling(ry);
                if (cx + ri < bx || cx - ri > bx + 15 || cz + ri < bz || cz - ri > bz + 15) continue;
                for (int dy = -ryi; dy <= ryi; dy++)
                {
                    int y = cy + dy;
                    if (y < 4 || y > 250) continue;
                    for (int dz = -ri; dz <= ri; dz++)
                        for (int dx = -ri; dx <= ri; dx++)
                        {
                            int lx = cx + dx - bx, lz = cz + dz - bz;
                            if ((uint)lx > 15 || (uint)lz > 15) continue;
                            double q = (dx * dx + dz * dz) / (r * r) + dy * dy / (ry * ry);
                            if (q > 1) continue;
                            int idx = lx + (lz << 4) + (y << 8);
                            byte old = c.Blocks[idx];
                            if (old == B.Stone || (soft && (old == B.Dirt || old == B.Sand || old == B.Gravel))) c.Blocks[idx] = b;
                        }
                }
            }
        }
    }
}
