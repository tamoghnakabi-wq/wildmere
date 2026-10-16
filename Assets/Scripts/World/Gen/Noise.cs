using System;
using System.Runtime.CompilerServices;

namespace Wildmere.World.Gen
{
    /// <summary>
    /// Seeded gradient (Perlin-style) noise in 2D and 3D. Deterministic for a seed, thread-safe after
    /// construction (read-only tables), roughly in [-1, 1].
    /// </summary>
    public sealed class Noise
    {
        readonly int[] mPerm = new int[512];
        readonly double mOffX, mOffY, mOffZ;

        static readonly double[] kGrad3 =
        {
            1,1,0, -1,1,0, 1,-1,0, -1,-1,0, 1,0,1, -1,0,1, 1,0,-1, -1,0,-1, 0,1,1, 0,-1,1, 0,1,-1, 0,-1,-1,
            1,1,0, -1,1,0, 0,-1,1, 0,-1,-1,
        };

        public Noise(long seed)
        {
            var rng = new SplitMix(seed);
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            for (int i = 255; i > 0; i--)
            {
                int j = (int)(rng.Next() % (ulong)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) mPerm[i] = p[i & 255];
            // A per-seed sub-cell offset hides lattice alignment between differently seeded layers.
            mOffX = rng.NextDouble() * 256.0; mOffY = rng.NextDouble() * 256.0; mOffZ = rng.NextDouble() * 256.0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Lerp(double a, double b, double t) => a + t * (b - a);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double G2(int h, double x, double y)
        {
            switch (h & 7)
            {
                case 0: return x + y;
                case 1: return -x + y;
                case 2: return x - y;
                case 3: return -x - y;
                case 4: return x;
                case 5: return -x;
                case 6: return y;
                default: return -y;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double G3(int h, double x, double y, double z)
        {
            int i = (h & 15) * 3;
            return kGrad3[i] * x + kGrad3[i + 1] * y + kGrad3[i + 2] * z;
        }

        public double Sample(double x, double y)
        {
            x += mOffX; y += mOffY;
            double fx = Math.Floor(x), fy = Math.Floor(y);
            int X = (int)fx & 255, Y = (int)fy & 255;
            x -= fx; y -= fy;
            double u = Fade(x), v = Fade(y);
            int[] p = mPerm;
            int a = p[X] + Y, b = p[X + 1] + Y;
            double r = Lerp(Lerp(G2(p[a], x, y), G2(p[b], x - 1, y), u),
                            Lerp(G2(p[a + 1], x, y - 1), G2(p[b + 1], x - 1, y - 1), u), v);
            return r * 0.7071;
        }

        public double Sample(double x, double y, double z)
        {
            x += mOffX; y += mOffY; z += mOffZ;
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
            x -= fx; y -= fy; z -= fz;
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int[] p = mPerm;
            int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
            int Bq = p[X + 1] + Y, BA = p[Bq] + Z, BB = p[Bq + 1] + Z;
            return Lerp(
                Lerp(Lerp(G3(p[AA], x, y, z), G3(p[BA], x - 1, y, z), u),
                     Lerp(G3(p[AB], x, y - 1, z), G3(p[BB], x - 1, y - 1, z), u), v),
                Lerp(Lerp(G3(p[AA + 1], x, y, z - 1), G3(p[BA + 1], x - 1, y, z - 1), u),
                     Lerp(G3(p[AB + 1], x, y - 1, z - 1), G3(p[BB + 1], x - 1, y - 1, z - 1), u), v), w);
        }

        /// <summary>Fractal sum, normalised to roughly [-1, 1]. Octaves are rotated to avoid grid artefacts.</summary>
        public double Fbm(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x, y) * amp;
                norm += amp;
                amp *= gain;
                // rotate ~37 degrees and scale
                double nx = (x * 0.7986 - y * 0.6018) * lacunarity;
                double ny = (x * 0.6018 + y * 0.7986) * lacunarity;
                x = nx + 17.3; y = ny - 9.1;
            }
            return sum / norm;
        }

        public double Fbm(double x, double y, double z, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x, y, z) * amp;
                norm += amp;
                amp *= gain;
                x *= lacunarity; y *= lacunarity; z *= lacunarity;
                x += 31.7; z -= 12.9;
            }
            return sum / norm;
        }

        /// <summary>Ridged multifractal in [0, 1]: sharp crests where the base noise crosses zero.</summary>
        public double Ridged(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0, amp = 1, norm = 0, weight = 1;
            for (int i = 0; i < octaves; i++)
            {
                double n = 1.0 - Math.Abs(Sample(x, y));
                n *= n;
                n *= weight;
                weight = Math.Min(1.0, Math.Max(0.0, n * 1.6));
                sum += n * amp;
                norm += amp;
                amp *= gain;
                double nx = (x * 0.7986 - y * 0.6018) * lacunarity;
                double ny = (x * 0.6018 + y * 0.7986) * lacunarity;
                x = nx + 5.3; y = ny + 11.7;
            }
            return sum / norm;
        }
    }

    /// <summary>Tiny deterministic PRNG (SplitMix64). Not thread-safe; create one per use.</summary>
    public struct SplitMix
    {
        ulong mState;
        public SplitMix(long seed) { mState = (ulong)seed ^ 0x9E3779B97F4A7C15UL; }

        public ulong Next()
        {
            ulong z = (mState += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
        public int Range(int min, int maxExclusive) => min + (int)(Next() % (ulong)(maxExclusive - min));
        public float Range(float min, float max) => (float)(min + NextDouble() * (max - min));

        /// <summary>Stable hash of integer coordinates and a salt (for per-position decisions).</summary>
        public static ulong Hash(long seed, int x, int z, int salt)
        {
            ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
            h ^= (ulong)(uint)x * 0xC2B2AE3D27D4EB4FUL;
            h = (h << 31) | (h >> 33);
            h ^= (ulong)(uint)z * 0x165667B19E3779F9UL;
            h = (h << 29) | (h >> 35);
            h ^= (ulong)(uint)salt * 0x27D4EB2F165667C5UL;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return h;
        }

        public static double Hash01(long seed, int x, int z, int salt) => (Hash(seed, x, z, salt) >> 11) * (1.0 / (1UL << 53));
    }
}
