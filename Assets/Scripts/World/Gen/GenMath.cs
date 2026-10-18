using System;
using System.Runtime.CompilerServices;

namespace Wildmere.World.Gen
{
    /// <summary>Small allocation-free math helpers shared by the generator (no Unity API: runs on worker threads).</summary>
    static class GenMath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Smoothstep from edge a to edge b (a may be greater than b for a falling step).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SS(double a, double b, double x)
        {
            double t = (x - a) / (b - a);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        /// <summary>Bilinear blend of four corner values (same arithmetic everywhere so cached and direct paths agree bit for bit).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Bilin(double v00, double v10, double v01, double v11, double tx, double tz)
        {
            double a = v00 + (v10 - v00) * tx;
            double b = v01 + (v11 - v01) * tx;
            return a + (b - a) * tz;
        }

        /// <summary>Map a roughly normal noise value (std <paramref name="sigma"/>) to an approximately uniform 0..1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Uniform(double v, double sigma)
        {
            double x = v / sigma;
            return 0.5 + 0.5 * Math.Tanh(0.7978845608 * x * (1 + 0.044715 * x * x));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FloorToInt(double v) { int i = (int)v; return v < i ? i - 1 : i; }

        /// <summary>Per-block hash in [0,1) including y (bedrock dither, ore choices...).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Hash3(long seed, int x, int y, int z, int salt)
            => SplitMix.Hash01(seed, x, z, salt * 1013 + y * 7919 + 17);
    }
}
