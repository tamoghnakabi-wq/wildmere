using System;
using System.Collections.Generic;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        public enum TreeType : byte { Oak, TallOak, Birch, Spruce, DarkOak, BigOak, Cactus }

        /// <summary>One tree decided from world coordinates (trunk origin = lowest log cell).</summary>
        public struct Tree
        {
            public int X, Y, Z;
            public TreeType Type;
            public ulong Hash;
        }

        const int TreeCell = 4;
        /// <summary>No tree block lies further than this from its trunk origin (horizontal, blocks).</summary>
        public const int TreeReach = 7;

        /// <summary>Trees whose blocks can reach chunk (cx, cz), in deterministic world order (tests / tools).</summary>
        public void CollectTrees(int cx, int cz, List<Tree> into)
        {
            var s = S;
            PrepareRegion(s, cx * 16, cz * 16);
            CollectTreesInto(s, cx * 16, cz * 16, into);
            into.Clear();
            into.AddRange(s.Trees);
        }

        void CollectTreesInto(Scratch s, int bx, int bz, List<Tree> unused)
        {
            var list = s.Trees;
            list.Clear();
            int ci0 = FloorDiv(bx - TreeReach - 1, TreeCell), ci1 = FloorDiv(bx + 15 + TreeReach, TreeCell);
            int cj0 = FloorDiv(bz - TreeReach - 1, TreeCell), cj1 = FloorDiv(bz + 15 + TreeReach, TreeCell);
            for (int cj = cj0; cj <= cj1; cj++)
                for (int ci = ci0; ci <= ci1; ci++)
                    if (TryTree(s, ci, cj, out var t)
                        && t.X + TreeReach + 1 >= bx && t.X - TreeReach <= bx + 15
                        && t.Z + TreeReach + 1 >= bz && t.Z - TreeReach <= bz + 15)
                        list.Add(t);
        }

        void PlaceTrees(Chunk ch, Scratch s, int bx, int bz)
        {
            CollectTreesInto(s, bx, bz, null);
            var list = s.Trees;
            for (int i = 0; i < list.Count; i++) BuildTree(ch, s, bx, bz, list[i]);
        }

        static bool TreeGround(byte b) => b == B.Grass || b == B.Podzol || b == B.SnowyGrass || b == B.Dirt;

        bool TryTree(Scratch s, int ci, int cj, out Tree t)
        {
            t = default;
            ulong h = SplitMix.Hash(Seed, ci, cj, 101);
            int x = ci * TreeCell + (int)(h % 3), z = cj * TreeCell + (int)((h >> 8) % 3);
            Col c = GetCol(s, x, z);
            if (c.Water > c.H || c.H < SeaLevel + 2 || c.H > 234 || (c.Flags & FBeach) != 0) return false;

            double tr = ((h >> 36) & 0xFFFF) / 65536.0;
            var clim = (Biome)c.Climate;
            TreeType type;
            switch (clim)
            {
                case Biome.DarkForest: type = tr < 0.88 ? TreeType.DarkOak : TreeType.TallOak; break;
                case Biome.TemperateForest:
                    if (c.T < 0.37f && tr < (0.37 - c.T) * 7) type = TreeType.Spruce;
                    else type = tr < 0.30 ? TreeType.Birch : tr < 0.58 ? TreeType.TallOak : TreeType.Oak;
                    break;
                case Biome.Plains: type = tr < 0.65 ? TreeType.BigOak : TreeType.Oak; break;
                case Biome.SnowyMountains: type = TreeType.Spruce; break;
                case Biome.Desert: type = TreeType.Cactus; break;
                default: type = TreeType.Oak; break;
            }
            if (c.H > 128 && type != TreeType.Spruce && type != TreeType.Cactus) type = TreeType.Spruce;

            double dens;
            if (type == TreeType.Cactus) dens = 0.05 * c.WDe;
            else
            {
                dens = c.WFo * 0.40 + c.WDk * 0.92 + c.WPl * 0.016;
                dens += c.WSn * 0.42 * (1 - SS(c.Snow - 34, c.Snow - 8, c.H));
                if (c.H > 125) dens *= 1 - SS(132, c.Snow - 4, c.H);
            }
            double r = ((h >> 16) & 0xFFFFF) / 1048576.0;
            if (r >= dens) return false;

            byte top = TopBlock(s, x, z);
            if (type == TreeType.Cactus ? top != B.Sand : !TreeGround(top)) return false;
            if (CarvedTop(s, x, z, c.H)) return false;

            int baseY = c.H + 1;
            if (type == TreeType.DarkOak)
            {
                int hmax = c.H, hmin = c.H;
                for (int k = 1; k < 4; k++)
                {
                    int nx = x + (k & 1), nz = z + (k >> 1);
                    Col n = GetCol(s, nx, nz);
                    if (n.Water > n.H || !TreeGround(TopBlock(s, nx, nz)) || CarvedTop(s, nx, nz, n.H)) return false;
                    if (n.H > hmax) hmax = n.H; if (n.H < hmin) hmin = n.H;
                }
                if (hmax - hmin > 1) return false;
                baseY = hmax + 1;
            }
            t = new Tree { X = x, Y = baseY, Z = z, Type = type, Hash = h };
            return true;
        }

        /// <summary>Does a cave open the top block of this column? (only possible in entrance zones)</summary>
        bool CarvedTop(Scratch s, int x, int z, int H)
        {
            if (GetCol(s, x, z).Ent <= 0 || CapOf(s, x, z) < H) return false;
            return CaveCarvedDirect(x, H, z);
        }

        /// <summary>Is any tree trunk within r blocks of (x, z)? Uses (and overwrites) this thread's chunk cache.</summary>
        bool TreeNear(int x, int z, int r)
        {
            var s = S;
            int cx = FloorDiv(x, 16), cz = FloorDiv(z, 16);
            PrepareRegion(s, cx * 16, cz * 16);
            CollectTreesInto(s, cx * 16, cz * 16, null);
            foreach (var t in s.Trees)
                if (Math.Abs(t.X - x) <= r + TreeReach - 3 && Math.Abs(t.Z - z) <= r + TreeReach - 3) return true;
            return false;
        }

        // ------------------------------------------------------------------ builders

        static void Log(Chunk c, int bx, int bz, int x, int y, int z, byte b)
        {
            int lx = x - bx, lz = z - bz;
            if ((uint)lx > 15 || (uint)lz > 15 || (uint)y > 254) return;
            int i = lx + (lz << 4) + (y << 8);
            byte o = c.Blocks[i];
            if (o == B.Air || (o >= B.LeavesOak && o <= B.LeavesDark) || Blocks.IsPlant(o)) c.Blocks[i] = b;
        }

        static void Leaf(Chunk c, int bx, int bz, int x, int y, int z, byte b)
        {
            int lx = x - bx, lz = z - bz;
            if ((uint)lx > 15 || (uint)lz > 15 || (uint)y > 254) return;
            int i = lx + (lz << 4) + (y << 8);
            if (c.Blocks[i] == B.Air) c.Blocks[i] = b;
        }

        static bool InReach(int bx, int bz, int x, int z, int r) => x + r >= bx && x - r <= bx + 15 && z + r >= bz && z - r <= bz + 15;

        static double H01(ulong seed, int a, int b, int c)
        {
            ulong h = seed ^ ((ulong)(uint)a * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)c * 0x165667B19E3779F9UL);
            h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29; h *= 0x94D049BB133111EBUL; h ^= h >> 32;
            return (h >> 11) * (1.0 / (1UL << 53));
        }

        void BuildTree(Chunk ch, Scratch s, int bx, int bz, in Tree t)
        {
            var r = new SplitMix((long)t.Hash);
            int x = t.X, y0 = t.Y, z = t.Z;
            switch (t.Type)
            {
                case TreeType.Oak:
                {
                    int top = y0 + 4 + r.Range(0, 3);
                    for (int y = y0; y <= top; y++) Log(ch, bx, bz, x, y, z, B.LogOak);
                    for (int dy = -2; dy <= 1; dy++)
                    {
                        int rad = dy <= -1 ? 2 : 1;
                        for (int dz = -rad; dz <= rad; dz++)
                            for (int dx = -rad; dx <= rad; dx++)
                            {
                                bool corner = Math.Abs(dx) == rad && Math.Abs(dz) == rad;
                                if (corner && (dy == 1 || H01(t.Hash, dx, dy, dz) < 0.5)) continue;
                                Leaf(ch, bx, bz, x + dx, top + dy, z + dz, B.LeavesOak);
                            }
                    }
                    break;
                }
                case TreeType.TallOak:
                {
                    int top = y0 + 7 + r.Range(0, 4);
                    for (int y = y0; y <= top; y++) Log(ch, bx, bz, x, y, z, B.LogOak);
                    double rx = 2.7 + r.NextDouble() * 0.7, ry = 2.7;
                    int nb = 1 + r.Range(0, 2);
                    for (int b = 0; b < nb; b++)
                    {
                        int dx = r.Range(0, 2) * 2 - 1, dz = r.Range(0, 2) * 2 - 1, by = top - 3 + b;
                        Log(ch, bx, bz, x + dx, by, z + dz, B.LogOak);
                        Log(ch, bx, bz, x + dx * 2, by + 1, z + dz * 2, B.LogOak);
                    }
                    Blob(ch, bx, bz, t.Hash, x + 0.0, top - 0.5, z + 0.0, rx, ry, B.LeavesOak, 0.28);
                    Blob(ch, bx, bz, t.Hash + 7, x + 0.0, top - 3.0, z + 0.0, rx - 0.6, 1.4, B.LeavesOak, 0.4);
                    break;
                }
                case TreeType.Birch:
                {
                    int top = y0 + 5 + r.Range(0, 4);
                    for (int y = y0; y <= top; y++) Log(ch, bx, bz, x, y, z, B.LogBirch);
                    for (int dy = -3; dy <= 1; dy++)
                    {
                        int rad = dy <= -2 ? 2 : 1;
                        for (int dz = -rad; dz <= rad; dz++)
                            for (int dx = -rad; dx <= rad; dx++)
                            {
                                bool corner = Math.Abs(dx) == rad && Math.Abs(dz) == rad;
                                if (corner && (dy >= 0 || H01(t.Hash, dx, dy, dz) < 0.6)) continue;
                                if (dy == 1 && (dx != 0 && dz != 0)) continue;
                                Leaf(ch, bx, bz, x + dx, top + dy, z + dz, B.LeavesBirch);
                            }
                    }
                    break;
                }
                case TreeType.Spruce:
                {
                    int hgt = 7 + r.Range(0, 7);
                    int top = y0 + hgt - 1;
                    for (int y = y0; y <= top; y++) Log(ch, bx, bz, x, y, z, B.LogSpruce);
                    int crownBase = y0 + 2 + r.Range(0, 2);
                    double rmax = hgt >= 11 ? 3.2 : 2.4;
                    for (int y = top + 2; y >= crownBase; y--)
                    {
                        int d = top + 2 - y;
                        double rr = Math.Min(rmax, 0.5 + d * 0.45);
                        if (d > 2 && d % 3 == 0) rr -= 1.1;
                        int ri = (int)Math.Ceiling(rr);
                        for (int dz = -ri; dz <= ri; dz++)
                            for (int dx = -ri; dx <= ri; dx++)
                                if (dx * dx + dz * dz <= rr * rr + 0.45) Leaf(ch, bx, bz, x + dx, y, z + dz, B.LeavesSpruce);
                    }
                    break;
                }
                case TreeType.DarkOak:
                {
                    int top = y0 + 5 + r.Range(0, 4);
                    for (int k = 0; k < 4; k++)
                    {
                        int tx = x + (k & 1), tz = z + (k >> 1);
                        if (!InReach(bx, bz, tx, tz, 0)) continue;
                        int g = GetCol(s, tx, tz).H;
                        for (int y = g + 1; y <= top; y++) Log(ch, bx, bz, tx, y, tz, B.LogDark);
                    }
                    double cx = x + 0.5, cz = z + 0.5;
                    // branches with leaf clusters
                    int nb = 2 + r.Range(0, 3);
                    for (int b = 0; b < nb; b++)
                    {
                        double a = r.NextDouble() * Math.PI * 2;
                        int len = 2 + r.Range(0, 2);
                        double ex = 0, ez = 0; int ey = top;
                        for (int k = 1; k <= len; k++)
                        {
                            ex = cx + Math.Cos(a) * (k + 0.6); ez = cz + Math.Sin(a) * (k + 0.6); ey = top - 1 + (k + 1) / 2;
                            Log(ch, bx, bz, (int)Math.Floor(ex), ey, (int)Math.Floor(ez), B.LogDark);
                        }
                        Blob(ch, bx, bz, t.Hash + (ulong)b * 31, Math.Floor(ex), ey + 1, Math.Floor(ez), 2.2, 1.5, B.LeavesDark, 0.35);
                    }
                    double rad0 = 4.4 + r.NextDouble() * 0.9;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        double rad = dy == -2 ? rad0 - 1.6 : dy == -1 ? rad0 - 0.3 : dy == 0 ? rad0 : dy == 1 ? rad0 - 0.9 : rad0 - 2.6;
                        int ri = (int)Math.Ceiling(rad);
                        int y = top + dy;
                        for (int pz = z - ri; pz <= z + 1 + ri; pz++)
                            for (int px = x - ri; px <= x + 1 + ri; px++)
                            {
                                double ddx = px + 0.5 - cx - 0.5, ddz = pz + 0.5 - cz - 0.5;
                                double d = Math.Sqrt(ddx * ddx + ddz * ddz);
                                if (d > rad) continue;
                                if (d > rad - 1.2 && H01(t.Hash, px, y, pz) < (dy == -2 ? 0.65 : 0.35)) continue;
                                Leaf(ch, bx, bz, px, y, pz, B.LeavesDark);
                            }
                    }
                    break;
                }
                case TreeType.BigOak:
                {
                    int top = y0 + 5 + r.Range(0, 3);
                    for (int y = y0; y <= top; y++) Log(ch, bx, bz, x, y, z, B.LogOak);
                    int nb = 3 + r.Range(0, 3);
                    for (int b = 0; b < nb; b++)
                    {
                        double a = (b + r.NextDouble() * 0.6) * Math.PI * 2 / nb;
                        int len = 2 + r.Range(0, 2), by = top - 2 + r.Range(0, 2);
                        double ex = x, ez = z; int ey = by;
                        for (int k = 1; k <= len; k++)
                        {
                            ex = x + 0.5 + Math.Cos(a) * k; ez = z + 0.5 + Math.Sin(a) * k; ey = by + k / 2;
                            Log(ch, bx, bz, (int)Math.Floor(ex), ey, (int)Math.Floor(ez), B.LogOak);
                        }
                        Blob(ch, bx, bz, t.Hash + (ulong)b * 17, Math.Floor(ex), ey + 0.5, Math.Floor(ez), 2.4, 1.8, B.LeavesOak, 0.3);
                    }
                    Blob(ch, bx, bz, t.Hash + 99, x, top + 0.5, z, 3.0, 2.3, B.LeavesOak, 0.3);
                    break;
                }
                case TreeType.Cactus:
                {
                    int hgt = 1 + r.Range(0, 3);
                    for (int y = y0; y < y0 + hgt; y++) Log(ch, bx, bz, x, y, z, B.Cactus);
                    break;
                }
            }
        }

        /// <summary>Ellipsoid of leaves around (cx, cy, cz) with a dithered edge.</summary>
        static void Blob(Chunk ch, int bx, int bz, ulong seed, double cx, double cy, double cz, double rx, double ry, byte leaf, double dither)
        {
            int rxi = (int)Math.Ceiling(rx), ryi = (int)Math.Ceiling(ry);
            int ix = (int)Math.Floor(cx), iz = (int)Math.Floor(cz), iy = (int)Math.Floor(cy);
            if (!InReach(bx, bz, ix, iz, rxi + 1)) return;
            for (int y = iy - ryi; y <= iy + ryi + 1; y++)
                for (int pz = iz - rxi; pz <= iz + rxi; pz++)
                    for (int px = ix - rxi; px <= ix + rxi; px++)
                    {
                        int lx = px - bx, lz = pz - bz;
                        if ((uint)lx > 15 || (uint)lz > 15) continue;
                        double dx = (px - cx) / rx, dz = (pz - cz) / rx, dy = (y - cy) / ry;
                        double q = dx * dx + dy * dy + dz * dz;
                        if (q > 1) continue;
                        if (q > 0.55 && H01(seed, px, y, pz) < dither) continue;
                        Leaf(ch, bx, bz, px, y, pz, leaf);
                    }
        }
    }
}
