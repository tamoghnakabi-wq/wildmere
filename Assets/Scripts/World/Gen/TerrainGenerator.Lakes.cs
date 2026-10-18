using System;
using System.Collections.Generic;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        /// <summary>A lake with its own water level (forest ponds, frozen alpine tarns, rare desert oases).</summary>
        internal struct Lake
        {
            public bool Valid;
            public int X, Z, Level;
            public float R, Depth;
            public bool Frozen;
        }

        const int LakeCell = 176, LakeJitterMin = 40, LakeJitterSpan = 96;
        const double LakeReach = 1.9;   // influence radius in units of R (bed + rim + shape noise)

        Lake GetLake(int cx, int cz)
        {
            long key = ((long)cx << 32) ^ (uint)cz;
            if (mLakes.TryGetValue(key, out var l)) return l;
            l = ComputeLake(cx, cz);
            mLakes.TryAdd(key, l);
            return l;
        }

        Lake ComputeLake(int cx, int cz)
        {
            var l = new Lake();
            ulong h = SplitMix.Hash(Seed, cx, cz, 501);
            double p = (h & 0xFFFF) / 65536.0;
            if (p >= 0.62) return l;
            l.X = cx * LakeCell + LakeJitterMin + (int)((h >> 16) % LakeJitterSpan);
            l.Z = cz * LakeCell + LakeJitterMin + (int)((h >> 28) % LakeJitterSpan);
            l.R = 8f + 15f * (float)(((h >> 40) & 0xFFF) / 4096.0);
            l.Depth = 2.5f + l.R * 0.22f;

            var c = SampleColumnInternal(l.X, l.Z, false);
            if (c.Water > c.H || c.H > 196 || c.H < SeaLevel + 3 || c.Land < 0.9f) return l;
            if (c.WDe > 0.5f && p > 0.10) return l;                       // deserts: rare oases only
            int min = c.H, max = c.H;
            for (int k = 0; k < 12; k++)
            {
                double a = k * (Math.PI * 2 / 12);
                double rr = k < 6 ? l.R * 1.05 : l.R * 1.7;
                var n = SampleColumnInternal(l.X + (int)Math.Round(Math.Cos(a) * rr), l.Z + (int)Math.Round(Math.Sin(a) * rr), false);
                if (n.Water > n.H || (n.Flags & FChannel) != 0) return l;  // keep away from rivers / sea
                if (k < 6) { if (n.H < min) min = n.H; if (n.H > max) max = n.H; }
            }
            if (max - min > 11) return l;                                  // too steep for a level lake
            l.Level = min - 1;
            if (l.Level < SeaLevel + 2) return l;
            l.Frozen = c.T < 0.20f || c.WSn > 0.4f || l.Level > 135;
            l.Valid = true;
            return l;
        }

        void GatherLakes(int x0, int z0, int x1, int z1, List<Lake> into)
        {
            into.Clear();
            int reach = (int)(23 * LakeReach * 1.3) + 2;
            int ci0 = FloorDiv(x0 - reach - LakeJitterMin - LakeJitterSpan, LakeCell), ci1 = FloorDiv(x1 + reach - LakeJitterMin, LakeCell);
            int cj0 = FloorDiv(z0 - reach - LakeJitterMin - LakeJitterSpan, LakeCell), cj1 = FloorDiv(z1 + reach - LakeJitterMin, LakeCell);
            for (int cj = cj0; cj <= cj1; cj++)
                for (int ci = ci0; ci <= ci1; ci++)
                {
                    var l = GetLake(ci, cj);
                    if (!l.Valid) continue;
                    double r = l.R * LakeReach * 1.3;
                    if (l.X + r < x0 || l.X - r > x1 || l.Z + r < z0 || l.Z - r > z1) continue;
                    into.Add(l);
                }
        }

        void ApplyLakes(int x, int z, List<Lake> lakes, ref Col c)
        {
            for (int i = 0; i < lakes.Count; i++)
            {
                var l = lakes[i];
                double dx = x - l.X, dz = z - l.Z, d2 = dx * dx + dz * dz;
                double reach = l.R * LakeReach;
                if (d2 > reach * reach * 1.69) continue;
                double shape = l.R * (1 + 0.24 * nLakeShape.Sample(x / 13.0, z / 13.0));
                double de = Math.Sqrt(d2) / shape;
                if (de < 1)
                {
                    double bed = l.Level - (1.2 + (l.Depth - 1.2) * (1 - de * de));
                    int b = FloorToInt(bed);
                    if (c.H > b) c.H = b;
                    if (c.Water < l.Level) c.Water = l.Level;
                    c.Flags |= FLake;
                    if (l.Frozen) c.Flags |= FFrozen;
                }
                else
                {
                    // Embankment: keep every face neighbour of the water at or above the water level.
                    double edge = 1 + 2.2 / shape;
                    double rim = de < edge ? l.Level + 1 : l.Level + 1 - (de - edge) * shape * 0.9;
                    int ri = FloorToInt(rim);
                    if (c.H < ri) c.H = ri;
                    if (c.H >= l.Level && c.Water > c.H) c.Water = 0;   // sea-level water under a raised rim
                }
            }
        }
    }
}
