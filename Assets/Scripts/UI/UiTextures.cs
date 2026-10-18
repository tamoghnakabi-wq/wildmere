using UnityEngine;

namespace Wildmere.UI
{
    /// <summary>
    /// Procedurally generated UI textures (no image assets needed): soft panel shadow, vignettes,
    /// gradients and soft light dots. Built once, on first use.
    /// </summary>
    public static class UiTextures
    {
        static Texture2D sShadow, sMenuShade, sBottomShade, sVignette, sDamage, sDot, sLowHealth;

        public static Texture2D PanelShadow => sShadow ? sShadow : sShadow = BuildShadow();
        public static Texture2D MenuShade => sMenuShade ? sMenuShade : sMenuShade = Horizontal(new Color(0.02f, 0.03f, 0.045f), 0.8f, 0f, 0.75f);
        public static Texture2D BottomShade => sBottomShade ? sBottomShade : sBottomShade = Vertical(new Color(0.02f, 0.03f, 0.045f), 0.7f, 0f, 1.6f);
        public static Texture2D Vignette => sVignette ? sVignette : sVignette = Radial(new Color(0.01f, 0.015f, 0.02f), 0.55f, 1.45f, 0.78f, 256);
        public static Texture2D Damage => sDamage ? sDamage : sDamage = Radial(new Color(0.62f, 0.04f, 0.05f), 0.42f, 1.35f, 0.72f, 256);
        public static Texture2D LowHealth => sLowHealth ? sLowHealth : sLowHealth = Radial(new Color(0.45f, 0.02f, 0.03f), 0.6f, 1.5f, 0.75f, 256);
        public static Texture2D SoftDot => sDot ? sDot : sDot = Dot(64);

        static Texture2D New(int w, int h, string name)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
            };
        }

        /// <summary>
        /// UI Toolkit blends in linear space in this (Linear colour space) project, which makes dark
        /// translucent overlays look much weaker than the same alpha in CSS. Map a perceptual alpha
        /// to the linear-blend alpha that looks the same.
        /// </summary>
        public static float DarkAlpha(float a) => 1f - Mathf.Pow(1f - Mathf.Clamp01(a), 2.2f);

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        static float RoundRectDistance(float px, float py, float x0, float y0, float x1, float y1, float r)
        {
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            float hx = (x1 - x0) * 0.5f - r, hy = (y1 - y0) * 0.5f - r;
            float qx = Mathf.Abs(px - cx) - hx, qy = Mathf.Abs(py - cy) - hy;
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        /// <summary>
        /// 9-sliced shadow (slices 64). The panel sits 44 px in from the left/right, 30 px from the top and
        /// 58 px from the bottom (see .panel-shadow); the shadow is hollow so translucent panels stay clean.
        /// </summary>
        static Texture2D BuildShadow()
        {
            const int W = 160, H = 160;
            var t = New(W, H, "UiPanelShadow");
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f; // top-down
                    float dPanel = RoundRectDistance(fx, fy, 44, 30, W - 44, H - 58, 20);
                    float dShadow = RoundRectDistance(fx, fy, 46, 40, W - 46, H - 50, 26);
                    float a = 0.62f * (1f - Smooth(-14f, 34f, dShadow));
                    a *= Smooth(-1.5f, 1.5f, dPanel);
                    px[(H - 1 - y) * W + x] = new Color32(0, 0, 0, (byte)(DarkAlpha(a) * 255));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D Horizontal(Color c, float a0, float a1, float curve)
        {
            const int W = 256, H = 4;
            var t = New(W, H, "UiGradH");
            var px = new Color32[W * H];
            for (int x = 0; x < W; x++)
            {
                float u = x / (W - 1f);
                float a = Mathf.Lerp(a0, a1, Mathf.Pow(Smooth(0f, 1f, u), 1f / curve));
                var col = new Color(c.r, c.g, c.b, DarkAlpha(a));
                for (int y = 0; y < H; y++) px[y * W + x] = col;
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D Vertical(Color c, float aBottom, float aTop, float curve)
        {
            const int W = 4, H = 256;
            var t = New(W, H, "UiGradV");
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float v = y / (H - 1f); // 0 = bottom
                float a = Mathf.Lerp(aBottom, aTop, Mathf.Pow(Smooth(0f, 1f, v), 1f / curve));
                var col = new Color(c.r, c.g, c.b, DarkAlpha(a));
                for (int x = 0; x < W; x++) px[y * W + x] = col;
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D Radial(Color c, float inner, float outer, float alpha, int size)
        {
            var t = New(size, size, "UiVignette");
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Pow(Smooth(inner, outer, d), 1.4f) * alpha;
                    px[y * size + x] = new Color(c.r, c.g, c.b, DarkAlpha(a));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D Dot(int size)
        {
            var t = New(size, size, "UiSoftDot");
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float d2 = u * u + v * v;
                    float a = Mathf.Exp(-d2 * 4.2f) * (1f - Smooth(0.85f, 1f, Mathf.Sqrt(d2)));
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }
    }
}
