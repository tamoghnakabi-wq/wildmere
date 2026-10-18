using UnityEngine;
using UnityEngine.UIElements;

namespace Wildmere.UI
{
    public enum GlyphKind { Emblem, Heart, Check, Dice, Trash, Play, Back, Plus, Close, Gear }

    /// <summary>
    /// Crisp vector icons drawn with Painter2D (resolution independent). Colour comes from the
    /// element's resolved USS text colour, so classes like .t-accent recolour them.
    /// </summary>
    public sealed class Glyph : VisualElement
    {
        GlyphKind mKind;
        static readonly Color kAccent = new Color(125 / 255f, 220 / 255f, 188 / 255f);
        static readonly Color kHealth = new Color(240 / 255f, 96 / 255f, 93 / 255f);

        public GlyphKind Kind { get => mKind; set { mKind = value; MarkDirtyRepaint(); } }

        public Glyph(GlyphKind kind, params string[] classes)
        {
            mKind = kind;
            pickingMode = PickingMode.Ignore;
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) AddToClassList(c);
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        void Draw(MeshGenerationContext ctx)
        {
            float w = contentRect.width, h = contentRect.height;
            if (w < 1f || h < 1f) return;
            float s = Mathf.Min(w, h);
            var o = new Vector2((w - s) * 0.5f, (h - s) * 0.5f);
            Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);
            var p = ctx.painter2D;
            var col = resolvedStyle.color;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (mKind)
            {
                case GlyphKind.Emblem:
                    // Wildmere mark: a ring holding twin peaks over still water.
                    p.strokeColor = col; p.lineWidth = s * 0.05f;
                    p.BeginPath(); p.Arc(P(0.5f, 0.5f), s * 0.45f, Angle.Degrees(0), Angle.Degrees(360)); p.ClosePath(); p.Stroke();
                    p.fillColor = kAccent;
                    p.BeginPath();
                    p.MoveTo(P(0.19f, 0.64f)); p.LineTo(P(0.4f, 0.29f)); p.LineTo(P(0.51f, 0.45f));
                    p.LineTo(P(0.61f, 0.33f)); p.LineTo(P(0.81f, 0.64f)); p.ClosePath(); p.Fill();
                    p.fillColor = new Color(1, 1, 1, 0.85f);
                    p.BeginPath(); p.MoveTo(P(0.4f, 0.29f)); p.LineTo(P(0.455f, 0.38f)); p.LineTo(P(0.4f, 0.355f)); p.LineTo(P(0.35f, 0.37f)); p.ClosePath(); p.Fill();
                    p.strokeColor = new Color(col.r, col.g, col.b, col.a * 0.75f); p.lineWidth = s * 0.05f;
                    p.BeginPath(); p.MoveTo(P(0.27f, 0.735f)); p.LineTo(P(0.73f, 0.735f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.37f, 0.83f)); p.LineTo(P(0.63f, 0.83f)); p.Stroke();
                    break;

                case GlyphKind.Heart:
                    p.fillColor = kHealth;
                    p.BeginPath();
                    p.MoveTo(P(0.5f, 0.88f));
                    p.BezierCurveTo(P(0.2f, 0.66f), P(0.04f, 0.47f), P(0.09f, 0.29f));
                    p.BezierCurveTo(P(0.15f, 0.08f), P(0.42f, 0.06f), P(0.5f, 0.27f));
                    p.BezierCurveTo(P(0.58f, 0.06f), P(0.85f, 0.08f), P(0.91f, 0.29f));
                    p.BezierCurveTo(P(0.96f, 0.47f), P(0.8f, 0.66f), P(0.5f, 0.88f));
                    p.ClosePath(); p.Fill();
                    p.fillColor = new Color(1, 1, 1, 0.35f);
                    p.BeginPath(); p.Arc(P(0.29f, 0.32f), s * 0.07f, Angle.Degrees(0), Angle.Degrees(360)); p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Check:
                    p.strokeColor = col; p.lineWidth = s * 0.12f;
                    p.BeginPath(); p.MoveTo(P(0.2f, 0.53f)); p.LineTo(P(0.41f, 0.73f)); p.LineTo(P(0.8f, 0.3f)); p.Stroke();
                    break;

                case GlyphKind.Dice:
                    p.strokeColor = col; p.lineWidth = s * 0.08f;
                    RoundRect(p, P(0.14f, 0.14f), s * 0.72f, s * 0.72f, s * 0.16f); p.Stroke();
                    p.fillColor = col;
                    foreach (var d in new[] { new Vector2(0.35f, 0.35f), new Vector2(0.65f, 0.65f), new Vector2(0.5f, 0.5f), new Vector2(0.35f, 0.65f), new Vector2(0.65f, 0.35f) })
                    { p.BeginPath(); p.Arc(P(d.x, d.y), s * 0.06f, Angle.Degrees(0), Angle.Degrees(360)); p.ClosePath(); p.Fill(); }
                    break;

                case GlyphKind.Trash:
                    p.strokeColor = col; p.lineWidth = s * 0.08f;
                    p.BeginPath(); p.MoveTo(P(0.17f, 0.27f)); p.LineTo(P(0.83f, 0.27f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.38f, 0.26f)); p.LineTo(P(0.41f, 0.15f)); p.LineTo(P(0.59f, 0.15f)); p.LineTo(P(0.62f, 0.26f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.26f, 0.35f)); p.LineTo(P(0.31f, 0.85f)); p.LineTo(P(0.69f, 0.85f)); p.LineTo(P(0.74f, 0.35f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.44f, 0.45f)); p.LineTo(P(0.45f, 0.74f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.56f, 0.45f)); p.LineTo(P(0.55f, 0.74f)); p.Stroke();
                    break;

                case GlyphKind.Play:
                    p.fillColor = col;
                    p.BeginPath(); p.MoveTo(P(0.32f, 0.2f)); p.LineTo(P(0.8f, 0.5f)); p.LineTo(P(0.32f, 0.8f)); p.ClosePath(); p.Fill();
                    break;

                case GlyphKind.Back:
                    p.strokeColor = col; p.lineWidth = s * 0.1f;
                    p.BeginPath(); p.MoveTo(P(0.8f, 0.5f)); p.LineTo(P(0.22f, 0.5f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.46f, 0.26f)); p.LineTo(P(0.22f, 0.5f)); p.LineTo(P(0.46f, 0.74f)); p.Stroke();
                    break;

                case GlyphKind.Plus:
                    p.strokeColor = col; p.lineWidth = s * 0.1f;
                    p.BeginPath(); p.MoveTo(P(0.5f, 0.2f)); p.LineTo(P(0.5f, 0.8f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.2f, 0.5f)); p.LineTo(P(0.8f, 0.5f)); p.Stroke();
                    break;

                case GlyphKind.Close:
                    p.strokeColor = col; p.lineWidth = s * 0.1f;
                    p.BeginPath(); p.MoveTo(P(0.25f, 0.25f)); p.LineTo(P(0.75f, 0.75f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(0.75f, 0.25f)); p.LineTo(P(0.25f, 0.75f)); p.Stroke();
                    break;

                case GlyphKind.Gear:
                    p.strokeColor = col; p.lineWidth = s * 0.08f;
                    p.BeginPath(); p.Arc(P(0.5f, 0.5f), s * 0.16f, Angle.Degrees(0), Angle.Degrees(360)); p.ClosePath(); p.Stroke();
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                        p.BeginPath(); p.MoveTo(P(0.5f, 0.5f) + dir * s * 0.27f); p.LineTo(P(0.5f, 0.5f) + dir * s * 0.4f); p.Stroke();
                    }
                    break;
            }
        }

        public static void RoundRect(Painter2D p, Vector2 o, float w, float h, float r)
        {
            r = Mathf.Min(r, Mathf.Min(w, h) * 0.5f);
            p.BeginPath();
            p.MoveTo(o + new Vector2(r, 0));
            p.LineTo(o + new Vector2(w - r, 0));
            p.ArcTo(o + new Vector2(w, 0), o + new Vector2(w, r), r);
            p.LineTo(o + new Vector2(w, h - r));
            p.ArcTo(o + new Vector2(w, h), o + new Vector2(w - r, h), r);
            p.LineTo(o + new Vector2(r, h));
            p.ArcTo(o + new Vector2(0, h), o + new Vector2(0, h - r), r);
            p.LineTo(o + new Vector2(0, r));
            p.ArcTo(o, o + new Vector2(r, 0), r);
            p.ClosePath();
        }
    }

    /// <summary>Gapped cross-hair with a mining-progress ring.</summary>
    public sealed class Crosshair : VisualElement
    {
        float mProgress, mGap = 5f;
        bool mTarget;
        static readonly Color kAccent = new Color(125 / 255f, 220 / 255f, 188 / 255f);

        public Crosshair()
        {
            AddToClassList("crosshair");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        public void Set(bool target, float progress)
        {
            progress = Mathf.Clamp01(progress);
            float gap = target ? 6.5f : 5f;
            if (target == mTarget && Mathf.Abs(progress - mProgress) < 0.004f && Mathf.Abs(gap - mGap) < 0.01f) return;
            mTarget = target; mProgress = progress; mGap = Mathf.MoveTowards(mGap, gap, 0.6f);
            MarkDirtyRepaint();
        }

        public bool Animating => Mathf.Abs(mGap - (mTarget ? 6.5f : 5f)) > 0.01f;

        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var c = contentRect.center;
            p.lineCap = LineCap.Round;
            const float len = 7f;
            void Ticks(float width, Color col)
            {
                p.strokeColor = col; p.lineWidth = width;
                p.BeginPath(); p.MoveTo(c + new Vector2(0, -mGap)); p.LineTo(c + new Vector2(0, -mGap - len)); p.Stroke();
                p.BeginPath(); p.MoveTo(c + new Vector2(0, mGap)); p.LineTo(c + new Vector2(0, mGap + len)); p.Stroke();
                p.BeginPath(); p.MoveTo(c + new Vector2(-mGap, 0)); p.LineTo(c + new Vector2(-mGap - len, 0)); p.Stroke();
                p.BeginPath(); p.MoveTo(c + new Vector2(mGap, 0)); p.LineTo(c + new Vector2(mGap + len, 0)); p.Stroke();
            }
            Ticks(4.4f, new Color(0, 0, 0, 0.32f));
            Ticks(2.2f, new Color(1, 1, 1, 0.93f));
            if (mTarget)
            {
                p.fillColor = new Color(1, 1, 1, 0.9f);
                p.BeginPath(); p.Arc(c, 1.3f, Angle.Degrees(0), Angle.Degrees(360)); p.ClosePath(); p.Fill();
            }
            if (mProgress > 0.001f)
            {
                const float r = 20f;
                p.strokeColor = new Color(0, 0, 0, 0.35f); p.lineWidth = 4.5f;
                p.BeginPath(); p.Arc(c, r, Angle.Degrees(0), Angle.Degrees(360)); p.Stroke();
                p.strokeColor = new Color(1, 1, 1, 0.16f); p.lineWidth = 2.5f;
                p.BeginPath(); p.Arc(c, r, Angle.Degrees(0), Angle.Degrees(360)); p.Stroke();
                p.strokeColor = kAccent; p.lineWidth = 3f;
                p.BeginPath(); p.Arc(c, r, Angle.Degrees(-90), Angle.Degrees(-90 + 360f * mProgress)); p.Stroke();
            }
        }
    }

    /// <summary>
    /// A blocky (voxel-stepped) ridgeline silhouette for the animated menu/loading background.
    /// Its width is twice the screen and the pattern repeats every half, so translating it by
    /// -offset mod half-width scrolls forever.
    /// </summary>
    public sealed class RidgeLayer : VisualElement
    {
        readonly Color mColor;
        readonly float mStep, mBase, mAmp;
        readonly int mSeed;
        readonly bool mTrees;

        public float Period { get; private set; }

        public RidgeLayer(Color color, float step, float baseH, float amp, int seed, bool trees)
        {
            mColor = color; mStep = step; mBase = baseH; mAmp = amp; mSeed = seed; mTrees = trees;
            pickingMode = PickingMode.Ignore;
            AddToClassList("bg-ridge");
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        void Draw(MeshGenerationContext ctx)
        {
            float w = contentRect.width, h = contentRect.height;
            if (w < 2f || h < 2f) return;
            float half = w * 0.5f;
            int n = Mathf.Max(8, Mathf.RoundToInt(half / mStep));
            float sw = half / n;
            Period = half;
            var rng = new System.Random(mSeed);
            float p1 = (float)rng.NextDouble() * 6.28f, p2 = (float)rng.NextDouble() * 6.28f, p3 = (float)rng.NextDouble() * 6.28f;
            int k1 = 1 + rng.Next(2), k2 = 3 + rng.Next(3), k3 = 7 + rng.Next(5);
            var heights = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n * Mathf.PI * 2f;
                float v = 0.55f * Mathf.Sin(t * k1 + p1) + 0.3f * Mathf.Sin(t * k2 + p2) + 0.15f * Mathf.Sin(t * k3 + p3);
                float y = (mBase + mAmp * (v * 0.5f + 0.5f)) * h;
                heights[i] = Mathf.Round(y / (sw * 0.5f)) * (sw * 0.5f);
            }
            var p = ctx.painter2D;
            p.fillColor = mColor;
            p.BeginPath();
            p.MoveTo(new Vector2(0, h));
            for (int i = 0; i < n * 2; i++)
            {
                float y = h - heights[i % n];
                p.LineTo(new Vector2(i * sw, y));
                p.LineTo(new Vector2((i + 1) * sw, y));
            }
            p.LineTo(new Vector2(w, h));
            p.ClosePath();
            p.Fill();

            if (!mTrees) return;
            // Sparse blocky conifers on the crest.
            var trng = new System.Random(mSeed * 7 + 3);
            var treeAt = new bool[n];
            for (int i = 0; i < n; i++) treeAt[i] = trng.NextDouble() < 0.28;
            for (int i = 0; i < n * 2; i++)
            {
                if (!treeAt[i % n]) continue;
                float x = i * sw + sw * 0.5f, y = h - heights[i % n];
                float bw = sw * 0.9f;
                float th = sw * (2.2f + (i % n % 3) * 0.5f);
                p.BeginPath();
                p.MoveTo(new Vector2(x - bw * 0.5f, y));
                p.LineTo(new Vector2(x - bw * 0.5f, y - th * 0.45f));
                p.LineTo(new Vector2(x - bw * 0.32f, y - th * 0.45f));
                p.LineTo(new Vector2(x - bw * 0.32f, y - th * 0.8f));
                p.LineTo(new Vector2(x - bw * 0.14f, y - th * 0.8f));
                p.LineTo(new Vector2(x - bw * 0.14f, y - th));
                p.LineTo(new Vector2(x + bw * 0.14f, y - th));
                p.LineTo(new Vector2(x + bw * 0.14f, y - th * 0.8f));
                p.LineTo(new Vector2(x + bw * 0.32f, y - th * 0.8f));
                p.LineTo(new Vector2(x + bw * 0.32f, y - th * 0.45f));
                p.LineTo(new Vector2(x + bw * 0.5f, y - th * 0.45f));
                p.LineTo(new Vector2(x + bw * 0.5f, y));
                p.ClosePath();
                p.Fill();
            }
        }
    }
}
