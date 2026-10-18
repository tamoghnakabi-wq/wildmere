using System;
using System.Runtime.CompilerServices;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        // ------------------------------------------------------------------ lattice fields

        internal const int Lat = 4;   // lattice spacing (blocks)
        internal const int NF = 15;   // fields per lattice point
        const int F_T = 0, F_HUM = 1, F_CONT = 2, F_ERO = 3, F_RIDGE = 4, F_MASSIF = 5, F_RIVER = 6, F_HILL = 7,
            F_MESA = 8, F_DUNE = 9, F_DUNEMASK = 10, F_CLIFF = 11, F_CLIFFAMP = 12, F_ENT = 13, F_VAR = 14;

        // Measured standard deviations of the fBm sums (see art-source/worldgen/harness noisestats).
        internal const double Sig2 = 0.138, Sig3 = 0.121, Sig4 = 0.114;

        // River valley tuning (river field units -> blocks: about RiverK blocks per unit near a zero crossing).
        const double RiverK = 640.0;

        void LatticePoint(int X, int Z, double[] d, int o)
        {
            double wx = X + nWarpX.Fbm(X / 420.0, Z / 420.0, 2) * 120.0;
            double wz = Z + nWarpZ.Fbm(X / 420.0, Z / 420.0, 2) * 120.0;
            d[o + F_T] = nTemp.Fbm(wx / 1700.0, wz / 1700.0, 3);
            d[o + F_HUM] = nHum.Fbm(wx / 1300.0, wz / 1300.0, 3);
            d[o + F_CONT] = nCont.Fbm(wx / 2600.0, wz / 2600.0, 4);
            d[o + F_ERO] = nEro.Fbm(X / 700.0, Z / 700.0, 2);
            d[o + F_RIDGE] = nRidge.Ridged(X / 560.0, Z / 560.0, 5);
            d[o + F_MASSIF] = nMassif.Fbm(X / 1400.0, Z / 1400.0, 2);
            // Rivers: zero contour of a single smooth octave, domain-warped twice for meanders.
            double rx = X + nRWarpX.Fbm(X / 260.0, Z / 260.0, 2) * 75.0;
            double rz = Z + nRWarpZ.Fbm(X / 260.0, Z / 260.0, 2) * 75.0;
            rx += nRWarpX.Sample(rz / 85.0 + 31.7, rx / 85.0) * 20.0;
            rz += nRWarpZ.Sample(rx / 85.0, rz / 85.0 - 12.1) * 20.0;
            d[o + F_RIVER] = nRiver.Sample(rx / 820.0, rz / 820.0);
            d[o + F_HILL] = nHill.Fbm(X / 260.0, Z / 260.0, 4);
            d[o + F_MESA] = nMesa.Fbm(X / 230.0, Z / 230.0, 3);
            double du = X * 0.8 + Z * 0.6, dv = -X * 0.6 + Z * 0.8;
            d[o + F_DUNE] = nDune.Ridged(du / 80.0, dv / 32.0, 2);
            d[o + F_DUNEMASK] = nDuneMask.Sample(X / 450.0, Z / 450.0);
            d[o + F_CLIFF] = nCliff.Fbm(X / 380.0, Z / 380.0, 2);
            d[o + F_CLIFFAMP] = nCliffAmp.Sample(X / 650.0, Z / 650.0);
            d[o + F_ENT] = nEnt.Fbm(X / 150.0, Z / 150.0, 2);
            d[o + F_VAR] = nVar.Fbm(X / 190.0, Z / 190.0, 2);
        }

        // ------------------------------------------------------------------ columns

        internal const byte FRiver = 1, FLake = 2, FOcean = 4, FBeach = 8, FFrozen = 16, FChannel = 32;

        /// <summary>Everything the generator knows about one column before blocks are written.</summary>
        internal struct Col
        {
            public int H;          // top solid terrain block
            public int Water;      // water surface; water fills H+1..Water when Water > H
            public byte Biome;     // dominant biome (dithered), including Ocean/Beach/River overrides
            public byte Climate;   // dominant land biome (dithered), ignoring water
            public byte Flags;
            public float T, Hum;   // 0..1
            public float WPl, WFo, WDk, WDe, WSn;  // smooth climate weights (sum 1)
            public float Land;     // 0 ocean .. 1 inland
            public float Ent;      // > 0: caves may open to the sky here
            public float Var;      // -1..1 slow tint variation
            public float Snow;     // snow line (y)
            public float Mesa;     // 0..1 mesa plateau
            public float Base;     // continental base height (mesa strata reference)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Bi(double[] L, int o00, int o10, int o01, int o11, int f, double tx, double tz)
            => Bilin(L[o00 + f], L[o10 + f], L[o01 + f], L[o11 + f], tx, tz);

        static double ContSpline(double c)
        {
            // continentalness -> base terrain height
            if (c < -0.55) return 26 + (c + 1.0) / 0.45 * 8;          // 26..34 deep ocean
            if (c < -0.30) return 34 + (c + 0.55) / 0.25 * 12;        // 34..46
            if (c < -0.18) return 46 + (c + 0.30) / 0.12 * 9;         // 46..55
            if (c < -0.10) return 55 + (c + 0.18) / 0.08 * 6;         // 55..61 shelf
            if (c < -0.04) return 61 + (c + 0.10) / 0.06 * 3.5;       // 61..64.5 coast
            if (c < 0.10) return 64.5 + (c + 0.04) / 0.14 * 2.5;      // 64.5..67
            if (c < 0.40) return 67 + (c - 0.10) / 0.30 * 5;          // 67..72
            return 72 + Math.Min(1.0, (c - 0.40) / 0.60) * 8;         // 72..80
        }

        /// <summary>Terrain for one column from an interpolated lattice cell (no lakes yet).</summary>
        void EvalTerrain(int x, int z, double[] L, int o00, int o10, int o01, int o11, double tx, double tz, ref Col c)
        {
            double tn = Uniform(Bi(L, o00, o10, o01, o11, F_T, tx, tz), Sig3);
            double hn = Uniform(Bi(L, o00, o10, o01, o11, F_HUM, tx, tz), Sig3);
            double C = Bi(L, o00, o10, o01, o11, F_CONT, tx, tz) * (0.30 / Sig4) + 0.07;

            // Climate weights (a partition of unity).
            double wSn = SS(0.175, 0.13, tn);
            double wDe = SS(0.725, 0.775, tn) * SS(0.68, 0.60, hn);
            double wTemp = 1 - wSn - wDe; if (wTemp < 0) wTemp = 0;
            double pl = SS(0.39, 0.33, hn), dk = SS(0.665, 0.725, hn);
            double fo = 1 - pl - dk; if (fo < 0) fo = 0;
            double wPl = wTemp * pl, wFo = wTemp * fo, wDk = wTemp * dk;

            double land = SS(-0.20, -0.06, C);
            double baseH = ContSpline(C);
            double ero = SS(-0.45, 0.45, Bi(L, o00, o10, o01, o11, F_ERO, tx, tz) / Sig2 * 0.3);
            double hills = Bi(L, o00, o10, o01, o11, F_HILL, tx, tz) / Sig4 * 0.42;

            // Biome shapes.
            double hPl = baseH + 1.5 + hills * (3.0 + 6.0 * ero);
            double hFo = baseH + 3.0 + hills * (6.0 + 14.0 * ero);
            double hDk = baseH + 2.5 + hills * (5.0 + 9.0 * ero);
            double hDe = baseH + 1.0 + hills * 2.5, mesa = 0;
            if (wDe > 0)
            {
                double m = Bi(L, o00, o10, o01, o11, F_MESA, tx, tz) / Sig3 * 0.3;
                mesa = SS(0.26, 0.30, m) * SS(0.15, 0.6, wDe);
                double mesaH = 15.0 * mesa + 9.0 * SS(0.42, 0.45, m) * mesa;
                double dune = Bi(L, o00, o10, o01, o11, F_DUNE, tx, tz);
                double dunes = 9.0 * dune * dune * Math.Sqrt(dune) * SS(-0.25, 0.25, Bi(L, o00, o10, o01, o11, F_DUNEMASK, tx, tz));
                hDe += land * (dunes * (1 - mesa) + mesaH);
            }
            double nonSnow = wPl + wFo + wDk + wDe;
            double h = nonSnow > 1e-6 ? (wPl * hPl + wFo * hFo + wDk * hDk + wDe * hDe) / nonSnow : hFo;

            // Escarpments: crisp steps along a winding contour in some temperate regions.
            double cliffAmp = SS(0.10, 0.40, Bi(L, o00, o10, o01, o11, F_CLIFFAMP, tx, tz)) * land * (1 - wDe) * (0.4 + ero);
            if (cliffAmp > 0.001)
                h += cliffAmp * 9.0 * SS(-0.010, 0.010, Bi(L, o00, o10, o01, o11, F_CLIFF, tx, tz));

            // Mountains (cold regions; the height blend is wider than the surface biome blend = foothills).
            double mBlend = SS(0.30, 0.14, tn) * land;
            if (mBlend > 0)
            {
                double r = Bi(L, o00, o10, o01, o11, F_RIDGE, tx, tz);
                double mass = 0.62 + 0.38 * SS(-0.5, 0.5, Bi(L, o00, o10, o01, o11, F_MASSIF, tx, tz) / Sig2 * 0.3);
                double rr = Clamp01((r - 0.45) / 0.55);
                double hMo = 84 + 150 * mass * Math.Pow(rr, 1.6) + hills * 10;
                h += (hMo - h) * mBlend;
            }

            // Fine detail (per block).
            h += nDetail.Fbm(x / 26.0, z / 26.0, 2) * (1.0 + 1.6 * ero) * (1 - 0.6 * wDe);
            if (mBlend > 0.02 && h > 96)
                h += nDetail2.Fbm(x / 17.0, z / 17.0, 2) * 3.0 * mBlend * SS(100, 160, h);

            // Rivers: carve a valley whose walls are at most moderately steep, plus a channel below sea level.
            byte flags = 0;
            double rv = Math.Abs(Bi(L, o00, o10, o01, o11, F_RIVER, tx, tz)) * RiverK;   // ~distance in blocks
            double var = Bi(L, o00, o10, o01, o11, F_VAR, tx, tz) / Sig2 * 0.33;
            if (rv < 120 && land > 0)
            {
                double strength = land * (1 - SS(92, 134, h));
                if (strength > 0)
                {
                    double chan = 3.5 + 2.4 * (var + 1);                       // channel half width 3.5..8.3
                    double flat = 3 + 5 * ero;
                    double floorY = 63.6 + (1 - strength) * Math.Max(0, h - 63.6);
                    double wall = floorY + Math.Max(0, rv - chan - flat) * (0.55 + 0.35 * ero);
                    if (h > wall) h = wall;
                    if (rv < chan)
                    {
                        double u = rv / chan; u = 1 - u * u;
                        double bed = floorY - 2.1 - 4.0 * u;
                        if (h > bed) h = bed;
                        if (bed < SeaLevel) flags |= FChannel;
                    }
                }
            }

            int H = FloorToInt(h);
            if (H < 4) H = 4; else if (H > 250) H = 250;
            c.H = H;
            c.Water = H < SeaLevel ? SeaLevel : 0;
            c.T = (float)tn; c.Hum = (float)hn;
            c.WPl = (float)wPl; c.WFo = (float)wFo; c.WDk = (float)wDk; c.WDe = (float)wDe; c.WSn = (float)wSn;
            c.Land = (float)land;
            c.Ent = (float)(Bi(L, o00, o10, o01, o11, F_ENT, tx, tz) / Sig2 * 0.3 - 0.13);
            c.Var = (float)var;
            c.Mesa = (float)mesa;
            c.Base = (float)baseH;
            c.Snow = (float)(150 + 15 * nSnow.Sample(x / 70.0, z / 70.0));
            c.Flags = flags;
        }

        /// <summary>Dithered dominant biome + water/coast flags (after lakes).</summary>
        void FinishCol(int x, int z, ref Col c)
        {
            ulong hh = SplitMix.Hash(Seed, x, z, 61);
            double m = nDither.Sample(x / 7.0, z / 7.0);
            double best = -1; byte clim = (byte)World.Biome.Plains;
            void Try(float w, int sh, double sgn, World.Biome b, ref double bestRef, ref byte climRef)
            {
                if (w <= 0) return;
                double r = ((hh >> sh) & 0xFFF) / 4096.0 * 0.5 + (0.5 + 0.5 * m * sgn) * 0.5;
                double sc = w * (0.65 + 0.7 * r);
                if (sc > bestRef) { bestRef = sc; climRef = (byte)b; }
            }
            Try(c.WPl, 0, 1, World.Biome.Plains, ref best, ref clim);
            Try(c.WFo, 12, -1, World.Biome.TemperateForest, ref best, ref clim);
            Try(c.WDk, 24, 1, World.Biome.DarkForest, ref best, ref clim);
            Try(c.WDe, 36, 0.6, World.Biome.Desert, ref best, ref clim);
            Try(c.WSn, 48, -0.8, World.Biome.SnowyMountains, ref best, ref clim);
            c.Climate = clim;

            bool wet = c.Water > c.H;
            bool cold = c.T < 0.20f || c.WSn > 0.5f;
            if (wet)
            {
                if ((c.Flags & FLake) == 0)
                {
                    if (c.Base < 63f) c.Flags |= FOcean;
                    else if ((c.Flags & FChannel) != 0) c.Flags |= FRiver;
                    else c.Flags |= FLake;   // inland basin at sea level
                }
                if (cold) c.Flags |= FFrozen;
                c.Biome = (c.Flags & FOcean) != 0 ? (byte)World.Biome.Ocean : (byte)World.Biome.River;
            }
            else if (c.H <= SeaLevel + 2 && c.Land < 0.92f && c.Mesa < 0.2f)
            {
                c.Flags |= FBeach;
                c.Biome = (byte)World.Biome.Beach;
            }
            else c.Biome = clim;
        }

        // ------------------------------------------------------------------ cached access (chunk path)

        void PrepareRegion(Scratch s, int bx, int bz)
        {
            s.LatX0 = FloorDiv(bx - CP, Lat);
            s.LatZ0 = FloorDiv(bz - CP, Lat);
            var L = s.Lat;
            for (int j = 0; j < LatN; j++)
                for (int i = 0; i < LatN; i++)
                    LatticePoint((s.LatX0 + i) * Lat, (s.LatZ0 + j) * Lat, L, (i + j * LatN) * NF);
            s.ColX0 = bx - CP; s.ColZ0 = bz - CP;
            s.Stamp++;
            if (s.Stamp == int.MaxValue) { Array.Clear(s.ColStamp, 0, s.ColStamp.Length); s.Stamp = 1; }
            GatherLakes(bx - CP, bz - CP, bx + 16 + CP, bz + 16 + CP, s.Lakes);
        }

        /// <summary>Column inside the chunk's cached region (computed on first use).</summary>
        ref Col GetColRef(Scratch s, int x, int z)
        {
            int i = (x - s.ColX0) + (z - s.ColZ0) * CW;
            ref Col c = ref s.Cols[i];
            if (s.ColStamp[i] != s.Stamp)
            {
                s.ColStamp[i] = s.Stamp;
                int lx = FloorDiv(x, Lat), lz = FloorDiv(z, Lat);
                double tx = (x - lx * Lat) * 0.25, tz = (z - lz * Lat) * 0.25;
                int o00 = ((lx - s.LatX0) + (lz - s.LatZ0) * LatN) * NF;
                int o10 = o00 + NF, o01 = o00 + LatN * NF, o11 = o01 + NF;
                c = default;
                EvalTerrain(x, z, s.Lat, o00, o10, o01, o11, tx, tz, ref c);
                ApplyLakes(x, z, s.Lakes, ref c);
                FinishCol(x, z, ref c);
            }
            return ref c;
        }

        Col GetCol(Scratch s, int x, int z) => GetColRef(s, x, z);

        // ------------------------------------------------------------------ direct access (anywhere)

        /// <summary>Evaluate a column without a chunk cache (bit-identical to the cached path).</summary>
        internal Col SampleColumnInternal(int x, int z, bool lakes)
        {
            var s = S;
            int lx = FloorDiv(x, Lat), lz = FloorDiv(z, Lat);
            if (s.DLx != lx || s.DLz != lz || s.DSeed != Seed)
            {
                LatticePoint(lx * Lat, lz * Lat, s.DLat, 0);
                LatticePoint((lx + 1) * Lat, lz * Lat, s.DLat, NF);
                LatticePoint(lx * Lat, (lz + 1) * Lat, s.DLat, 2 * NF);
                LatticePoint((lx + 1) * Lat, (lz + 1) * Lat, s.DLat, 3 * NF);
                s.DLx = lx; s.DLz = lz; s.DSeed = Seed;
            }
            double tx = (x - lx * Lat) * 0.25, tz = (z - lz * Lat) * 0.25;
            var c = default(Col);
            EvalTerrain(x, z, s.DLat, 0, NF, 2 * NF, 3 * NF, tx, tz, ref c);
            if (lakes)
            {
                GatherLakes(x, z, x + 1, z + 1, s.DLakes);
                ApplyLakes(x, z, s.DLakes, ref c);
            }
            FinishCol(x, z, ref c);
            return c;
        }

        // ------------------------------------------------------------------ surface context for inner columns

        /// <summary>Highest y caves may carve in a column: sealed under and beside water, open to the sky only in entrance zones.</summary>
        int CapOf(Scratch s, int x, int z)
        {
            Col c = GetCol(s, x, z);
            int cap = c.H - 5;
            if (c.Ent > 0) cap += (int)(8 * SS(0, 0.18, c.Ent));
            if (c.Water > c.H) cap = Math.Min(cap, c.H - 6);
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                Col n = GetCol(s, nx, nz);
                if (n.Water > n.H) cap = Math.Min(cap, n.H - 6);
            }
            return cap;
        }

        void ComputeSurfaceContext(Scratch s, int bx, int bz)
        {
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wx = bx + x, wz = bz + z, i = x + z * 16;
                    Col c = GetCol(s, wx, wz);
                    Context(s, wx, wz, out int slope, out int nWater);
                    s.Slope[i] = (byte)Math.Min(255, slope);
                    s.NWater[i] = nWater;
                    int cap = CapOf(s, wx, wz);
                    s.Cap[i] = cap;
                }
        }
    }
}
