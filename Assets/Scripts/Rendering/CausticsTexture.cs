using UnityEngine;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Tileable caustic web generated once at startup: bright, slightly blurred lines along the edges of a
    /// jittered Voronoi diagram. The shaders take min() of two scrolling layers, which turns the web into the
    /// shifting light network seen on a pool floor.
    /// </summary>
    public static class CausticsTexture
    {
        static Texture2D sTex;
        const int N = 256, Cells = 7;

        public static Texture2D Get()
        {
            if (sTex != null) return sTex;
            var rng = new System.Random(4711);
            var pts = new Vector2[Cells * Cells];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
            var px = new byte[N * N];
            float cell = (float)N / Cells;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float fx = x / cell, fy = y / cell;
                    int cx = Mathf.FloorToInt(fx), cy = Mathf.FloorToInt(fy);
                    float f1 = 9, f2 = 9;
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int gx = cx + ox, gy = cy + oy;
                            var p = pts[((gx % Cells + Cells) % Cells) + ((gy % Cells + Cells) % Cells) * Cells];
                            float dx = gx + 0.15f + p.x * 0.7f - fx, dy = gy + 0.15f + p.y * 0.7f - fy;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) f2 = d;
                        }
                    float e = f2 - f1;                     // 0 on cell borders
                    float v = Mathf.Exp(-e * e * 90f) * 0.85f + Mathf.Exp(-e * 9f) * 0.25f;
                    px[x + y * N] = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                }
            sTex = new Texture2D(N, N, TextureFormat.R8, true, true)
            {
                name = "WM Caustics", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 2,
            };
            sTex.SetPixelData(px, 0);
            sTex.Apply(true, true);
            return sTex;
        }
    }
}
