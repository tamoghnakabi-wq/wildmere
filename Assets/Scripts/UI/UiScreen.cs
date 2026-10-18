using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wildmere.UI
{
    /// <summary>
    /// A full-screen layer with animated show/hide (fade + child drift via .anim-* classes) and
    /// keyboard focus handling. Screens are stacked by <see cref="UiRoot"/>; Esc calls <see cref="OnBack"/>.
    /// </summary>
    public abstract class UiScreen
    {
        public readonly VisualElement Root;
        public bool IsOpen { get; private set; }
        public abstract string Id { get; }
        protected UiRoot UI => UiRoot.Instance;
        IVisualElementScheduledItem mHideJob;

        /// <summary>Element focused when the screen opens (keyboard navigation starts here).</summary>
        protected VisualElement DefaultFocus;

        protected UiScreen()
        {
            Root = new VisualElement();
            Root.AddToClassList("screen");
            Root.AddToClassList("screen--hidden");
            Root.style.display = DisplayStyle.None;
            Root.pickingMode = PickingMode.Ignore;
        }

        /// <summary>Build the element tree once (called when the screen is registered).</summary>
        public abstract void Build();

        public void Show()
        {
            mHideJob?.Pause();
            bool wasOpen = IsOpen;
            IsOpen = true;
            Root.style.display = DisplayStyle.Flex;
            Root.BringToFront();
            OnShow();
            // One frame later so the transition starts from the hidden style.
            Root.schedule.Execute(() => { if (IsOpen) Root.RemoveFromClassList("screen--hidden"); }).ExecuteLater(16);
            if (!wasOpen) FocusDefault();
        }

        public void Hide(bool instant = false)
        {
            if (!IsOpen && Root.style.display == DisplayStyle.None) return;
            IsOpen = false;
            Root.AddToClassList("screen--hidden");
            var focused = Root.panel?.focusController?.focusedElement as VisualElement;
            if (focused != null && Root.Contains(focused)) focused.Blur();
            OnHide();
            mHideJob?.Pause();
            if (instant) { Root.style.display = DisplayStyle.None; return; }
            mHideJob = Root.schedule.Execute(() => { if (!IsOpen) Root.style.display = DisplayStyle.None; });
            mHideJob.ExecuteLater(260);
        }

        public void FocusDefault()
        {
            if (DefaultFocus == null) return;
            Root.schedule.Execute(() => { if (IsOpen && DefaultFocus.canGrabFocus) DefaultFocus.Focus(); }).ExecuteLater(30);
        }

        protected virtual void OnShow() { }
        protected virtual void OnHide() { }

        /// <summary>Esc / back. Return true when handled (default: close this screen).</summary>
        public virtual bool OnBack()
        {
            UI.Pop(this);
            return true;
        }

        /// <summary>Per-frame update while open (unscaled time).</summary>
        public virtual void Tick(float dt) { }

        /// <summary>True when this screen wants the mouse during play (inventory, death).</summary>
        public virtual bool CapturesMouse => true;
    }

    /// <summary>
    /// The animated fallback background (deep dusk gradient, drifting light, voxel-stepped parallax
    /// ridges and motes). Shown on the loading screen, and on the main menu until the live world
    /// backdrop is ready (see <see cref="UiRoot.SetBackdropActive"/>).
    /// </summary>
    public sealed class AnimatedBackground
    {
        public readonly VisualElement Root;
        readonly List<(VisualElement e, Vector2 basePos, Vector2 size, float speed, float phase)> mBlobs = new List<(VisualElement, Vector2, Vector2, float, float)>();
        readonly List<(RidgeLayer e, float speed, float parallax)> mRidges = new List<(RidgeLayer, float, float)>();
        readonly List<(VisualElement e, float x, float y, float speed, float phase, float size)> mMotes = new List<(VisualElement, float, float, float, float, float)>();
        float mT;
        Vector2 mMouse;

        public AnimatedBackground()
        {
            Root = Ui.Div("bg-anim", "fill").Ignore();
            Blob(new Color(0.16f, 0.52f, 0.5f, 0.55f), new Vector2(-0.12f, -0.25f), new Vector2(1.2f, 1.1f), 0.05f, 0f);
            Blob(new Color(0.93f, 0.55f, 0.32f, 0.35f), new Vector2(0.45f, 0.05f), new Vector2(1.0f, 0.9f), 0.04f, 2f);
            Blob(new Color(0.22f, 0.3f, 0.62f, 0.45f), new Vector2(0.15f, 0.25f), new Vector2(1.3f, 1.0f), 0.03f, 4f);
            Blob(new Color(0.98f, 0.78f, 0.5f, 0.22f), new Vector2(0.62f, 0.32f), new Vector2(0.5f, 0.45f), 0.06f, 1f);

            Ridge(new Color32(26, 52, 60, 255), 34f, 0.34f, 0.32f, 11, false, 5f, 6f);
            Ridge(new Color32(18, 37, 43, 255), 26f, 0.22f, 0.26f, 23, true, 11f, 14f);
            Ridge(new Color32(10, 22, 26, 255), 20f, 0.08f, 0.2f, 37, true, 20f, 26f);

            var rng = new System.Random(5);
            for (int i = 0; i < 28; i++)
            {
                var m = Root.Add(Ui.Div("bg-mote").Ignore());
                m.style.backgroundImage = UiTextures.SoftDot;
                float size = 4f + (float)rng.NextDouble() * 7f;
                m.style.width = size; m.style.height = size;
                m.style.unityBackgroundImageTintColor = new Color(1f, 0.92f, 0.75f, 1f);
                mMotes.Add((m, (float)rng.NextDouble(), (float)rng.NextDouble(), 6f + (float)rng.NextDouble() * 16f, (float)rng.NextDouble() * 10f, size));
            }
            var vig = Root.Add(Ui.Div("bg-vignette").Ignore());
            vig.style.backgroundImage = UiTextures.Vignette;
        }

        void Blob(Color c, Vector2 pos, Vector2 size, float speed, float phase)
        {
            var b = Root.Add(Ui.Div("bg-blob").Ignore());
            b.style.backgroundImage = UiTextures.SoftDot;
            b.style.unityBackgroundImageTintColor = c;
            mBlobs.Add((b, pos, size, speed, phase));
        }

        void Ridge(Color c, float step, float baseH, float amp, int seed, bool trees, float speed, float parallax)
        {
            var r = new RidgeLayer(c, step, baseH, amp, seed, trees);
            r.style.width = Length.Percent(200);
            Root.Add(r);
            mRidges.Add((r, speed, parallax));
        }

        public void Tick(float dt)
        {
            if (Root.resolvedStyle.display == DisplayStyle.None || Root.resolvedStyle.opacity <= 0.001f) return;
            mT += dt;
            float W = Root.layout.width, H = Root.layout.height;
            if (float.IsNaN(W) || W < 2f) return;
            var mp = (Vector2)Input.mousePosition;
            var target = new Vector2(mp.x / Mathf.Max(1, Screen.width) - 0.5f, 0.5f - mp.y / Mathf.Max(1, Screen.height));
            mMouse = Vector2.Lerp(mMouse, target, 1f - Mathf.Exp(-2.5f * dt));

            foreach (var (e, basePos, size, speed, phase) in mBlobs)
            {
                float ox = Mathf.Sin(mT * speed + phase) * 0.06f + Mathf.Sin(mT * speed * 0.37f + phase * 2f) * 0.04f;
                float oy = Mathf.Cos(mT * speed * 0.8f + phase) * 0.05f;
                e.style.left = (basePos.x + ox) * W - mMouse.x * 18f;
                e.style.top = (basePos.y + oy) * H - mMouse.y * 12f;
                e.style.width = size.x * W;
                e.style.height = size.y * H;
            }
            foreach (var (e, speed, parallax) in mRidges)
            {
                // Twice the screen plus parallax slack, so scrolling never exposes an edge.
                float want = 2f * W + 4f * parallax + 8f;
                if (Mathf.Abs(e.resolvedStyle.width - want) > 1f) e.style.width = want;
                float period = e.Period > 1f ? e.Period : W;
                float x = -Mathf.Repeat(mT * speed, period) - mMouse.x * parallax;
                e.style.translate = new Translate(x - parallax, mMouse.y * parallax * 0.4f);
            }
            foreach (var (e, x0, y0, speed, phase, size) in mMotes)
            {
                float y = Mathf.Repeat(y0 * H - mT * speed, H + 40f) - 20f;
                float x = x0 * W + Mathf.Sin(mT * 0.4f + phase) * 26f - mMouse.x * 30f;
                e.style.left = x; e.style.top = y;
                float fade = Mathf.Clamp01(y / (H * 0.25f)) * Mathf.Clamp01((H - y) / (H * 0.3f));
                e.style.opacity = fade * (0.35f + 0.35f * Mathf.Sin(mT * 1.3f + phase * 3f));
            }
        }
    }
}
