using System;
using System.Runtime.CompilerServices;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        const int CaveMinY = 4, CaveWaterY = 8;

        /// <summary>
        /// Cave noise at a world-aligned 4x4x4 lattice point: two smooth fields whose common zero set forms
        /// winding "spaghetti" tunnels, and a low-frequency "cheese" field for big caverns.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CaveLattice(int X, int Y, int Z, out double a, out double b, out double ch)
        {
            a = nCaveA.Sample(X / 58.0, Y / 30.0, Z / 58.0);
            b = nCaveB.Sample(X / 58.0, Y / 30.0, Z / 58.0);
            ch = nCheese.Fbm(X / 115.0, Y / 50.0, Z / 115.0, 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool CaveTest(double a, double b, double ch, int y, double ent, int surface)
        {
            double r2 = 0.0032 + 0.0018 * SS(70, 20, y);
            // entrance zones: tunnels swell near the surface so they break through as cave mouths
            if (ent > 0) r2 *= 1 + 2.2 * ent * SS(surface - 16, surface - 3, y);
            if (a * a + b * b < r2) return true;
            double thr = 0.40 + 0.6 * (SS(42, 60, y) + SS(15, 6, y));
            return ch > thr;
        }

        void CarveCaves(Chunk chunk, Scratch s, int bx, int bz)
        {
            int maxCap = 0;
            for (int i = 0; i < 256; i++) if (s.Cap[i] > maxCap) maxCap = s.Cap[i];
            if (maxCap < CaveMinY) return;
            if (maxCap > 250) maxCap = 250;
            int nk = (maxCap >> 2) + 2;
            double[] A = s.CaveA, Bv = s.CaveB, Cv = s.CaveC;
            for (int iz = 0; iz < 5; iz++)
                for (int ix = 0; ix < 5; ix++)
                {
                    int o = (ix + iz * 5) * 66;
                    for (int k = 0; k < nk; k++)
                    {
                        CaveLattice(bx + ix * 4, k * 4, bz + iz * 4, out double a, out double b, out double ch);
                        A[o + k] = a; Bv[o + k] = b; Cv[o + k] = ch;
                    }
                }

            var blocks = chunk.Blocks;
            double[] ca = s.ColA, cb = s.ColB, cc = s.ColC;
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int cap = s.Cap[x + z * 16];
                    if (cap < CaveMinY) continue;
                    Col cc0 = GetCol(s, bx + x, bz + z);
                    double ent = EntBoost(cc0.Ent);
                    int surface = cc0.H;
                    if (cap > maxCap) cap = maxCap;
                    int ix = x >> 2, iz = z >> 2;
                    double tx = (x & 3) * 0.25, tz = (z & 3) * 0.25;
                    int o00 = (ix + iz * 5) * 66, o10 = o00 + 66, o01 = o00 + 5 * 66, o11 = o01 + 66;
                    int kmax = (cap >> 2) + 1;
                    for (int k = 0; k <= kmax; k++)
                    {
                        ca[k] = Bilin(A[o00 + k], A[o10 + k], A[o01 + k], A[o11 + k], tx, tz);
                        cb[k] = Bilin(Bv[o00 + k], Bv[o10 + k], Bv[o01 + k], Bv[o11 + k], tx, tz);
                        cc[k] = Bilin(Cv[o00 + k], Cv[o10 + k], Cv[o01 + k], Cv[o11 + k], tx, tz);
                    }
                    int col = x + (z << 4);
                    for (int y = CaveMinY; y <= cap; y++)
                    {
                        int k = y >> 2; double ty = (y & 3) * 0.25;
                        double a = Lerp(ca[k], ca[k + 1], ty), b = Lerp(cb[k], cb[k + 1], ty), ch = Lerp(cc[k], cc[k + 1], ty);
                        if (!CaveTest(a, b, ch, y, ent, surface)) continue;
                        int idx = col + (y << 8);
                        byte old = blocks[idx];
                        if (old == B.Air || old == B.Water || old == B.Bedrock || old == B.Ice) continue;
                        blocks[idx] = y <= CaveWaterY ? B.Water : B.Air;
                    }
                }
        }

        /// <summary>Is (x,y,z) carved by caves (ignoring the column ceiling)? Same arithmetic as <see cref="CarveCaves"/>.</summary>
        bool CaveCarvedDirect(int x, int y, int z, float colEnt, int surface)
        {
            if (y < CaveMinY) return false;
            int X0 = FloorDiv(x, 4) * 4, Z0 = FloorDiv(z, 4) * 4, k = y >> 2;
            double tx = (x - X0) * 0.25, tz = (z - Z0) * 0.25, ty = (y & 3) * 0.25;
            double ak0, ak1, bk0, bk1, ck0, ck1;
            {
                CaveLattice(X0, k * 4, Z0, out double a00, out double b00, out double c00);
                CaveLattice(X0 + 4, k * 4, Z0, out double a10, out double b10, out double c10);
                CaveLattice(X0, k * 4, Z0 + 4, out double a01, out double b01, out double c01);
                CaveLattice(X0 + 4, k * 4, Z0 + 4, out double a11, out double b11, out double c11);
                ak0 = Bilin(a00, a10, a01, a11, tx, tz); bk0 = Bilin(b00, b10, b01, b11, tx, tz); ck0 = Bilin(c00, c10, c01, c11, tx, tz);
            }
            {
                int Y = (k + 1) * 4;
                CaveLattice(X0, Y, Z0, out double a00, out double b00, out double c00);
                CaveLattice(X0 + 4, Y, Z0, out double a10, out double b10, out double c10);
                CaveLattice(X0, Y, Z0 + 4, out double a01, out double b01, out double c01);
                CaveLattice(X0 + 4, Y, Z0 + 4, out double a11, out double b11, out double c11);
                ak1 = Bilin(a00, a10, a01, a11, tx, tz); bk1 = Bilin(b00, b10, b01, b11, tx, tz); ck1 = Bilin(c00, c10, c01, c11, tx, tz);
            }
            return CaveTest(Lerp(ak0, ak1, ty), Lerp(bk0, bk1, ty), Lerp(ck0, ck1, ty), y, EntBoost(colEnt), surface);
        }

        static double EntBoost(float ent) => ent > 0 ? SS(0, 0.25, ent) : 0;


    }
}
