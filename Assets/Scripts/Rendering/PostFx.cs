using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Wildmere.Core;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Global post-processing volume built at runtime: ACES tonemapping, bloom (sun, torches, glowcaps,
    /// lightning), per-frame colour grading driven by the atmosphere (warm golden hour, cool moonlit night
    /// with a gentle eye-adaptation lift, desaturated storms, green-grey dark forests, cyan underwater),
    /// split toning (cool shadows / warm highlights) and a subtle vignette.
    /// </summary>
    public static class PostFx
    {
        static Volume sVolume;
        static Bloom sBloom;
        static ColorAdjustments sColor;
        static Tonemapping sTone;
        static Vignette sVignette;
        static WhiteBalance sWhite;
        static SplitToning sSplit;
        static LiftGammaGain sLgg;
        static float sBrightness;

        public static Volume Volume => sVolume;

        static void Ensure()
        {
            if (sVolume != null) return;
            var go = new GameObject("PostFx");
            Object.DontDestroyOnLoad(go);
            sVolume = go.AddComponent<Volume>();
            sVolume.isGlobal = true;
            sVolume.priority = 10;
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            sVolume.sharedProfile = p;
            sTone = p.Add<Tonemapping>(true);
            sTone.mode.Override(TonemappingMode.ACES);
            sBloom = p.Add<Bloom>(true);
            sBloom.threshold.Override(1.05f);
            sBloom.intensity.Override(0.45f);
            sBloom.scatter.Override(0.66f);
            sBloom.clamp.Override(40f);
            sBloom.highQualityFiltering.Override(false);
            sColor = p.Add<ColorAdjustments>(true);
            sColor.postExposure.Override(0.0f);
            sColor.contrast.Override(10f);
            sColor.saturation.Override(8f);
            sColor.colorFilter.Override(Color.white);
            sWhite = p.Add<WhiteBalance>(true);
            sWhite.temperature.Override(2f);
            sWhite.tint.Override(0f);
            sSplit = p.Add<SplitToning>(true);
            sSplit.shadows.Override(new Color(0.42f, 0.48f, 0.6f));
            sSplit.highlights.Override(new Color(0.62f, 0.55f, 0.46f));
            sSplit.balance.Override(10f);
            sLgg = p.Add<LiftGammaGain>(true);
            sLgg.lift.Override(new Vector4(1f, 1f, 1f, 0f));
            sLgg.gamma.Override(new Vector4(1f, 1f, 1f, 0f));
            sLgg.gain.Override(new Vector4(1f, 1f, 1f, 0f));
            sVignette = p.Add<Vignette>(true);
            sVignette.intensity.Override(0.2f);
            sVignette.smoothness.Override(0.42f);
            sVignette.rounded.Override(false);
        }

        public static void Apply(GameSettings s)
        {
            Ensure();
            sBloom.active = s.Bloom;
            sBrightness = s.Brightness;
            RenderQuality.Apply(s);
            sBloom.highQualityFiltering.Override(RenderQuality.Level == GraphicsPreset.Ultra);
        }

        /// <summary>Per-frame grading from the atmosphere state (called by Atmosphere.Apply).</summary>
        public static void UpdateFrame(float day, float golden, float overcast, float storm, float darkForest, bool underwater, float flash, float sunH)
        {
            if (sVolume == null) return;
            float night = 1f - day;
            float blueHour = Mathf.Clamp01(1f - Mathf.Abs(sunH + 0.04f) / 0.06f) * (1f - golden * 0.5f);
            float grey = Mathf.Clamp01(overcast * 0.8f + storm * 0.4f);

            // Exposure: a gentle eye-adaptation lift at night and in dark forests, never a flat grey night.
            float exposure = -0.05f + night * 0.75f + darkForest * 0.3f + overcast * 0.2f + sBrightness * 0.8f;
            sColor.postExposure.Override(exposure);
            sColor.contrast.Override(Mathf.Lerp(12f, 4f, grey) + golden * 4f - night * 2f);
            sColor.saturation.Override(0f + golden * 10f - night * 22f - grey * 18f - darkForest * 8f + blueHour * 4f);
            sWhite.temperature.Override(2f + golden * 20f - night * 16f - blueHour * 6f - grey * 5f - darkForest * 3f + (underwater ? -18f : 0f));
            sWhite.tint.Override(-darkForest * 6f + golden * 2f + (underwater ? -10f : 0f));
            sSplit.balance.Override(Mathf.Lerp(15f, -20f, night));
            float splitAmt = Mathf.Clamp01(0.4f + golden * 0.6f + night * 0.3f - grey * 0.4f);
            sSplit.shadows.Override(Color.Lerp(Color.grey, new Color(0.36f, 0.45f, 0.64f), splitAmt));
            sSplit.highlights.Override(Color.Lerp(Color.grey, new Color(0.66f, 0.55f, 0.42f), splitAmt * (1f - night * 0.6f)));
            // Lift the blacks slightly blue at night (moonlit), keep them neutral by day.
            sLgg.lift.Override(new Vector4(1f - night * 0.02f, 1f, 1f + night * 0.04f, night * 0.02f));
            sColor.colorFilter.Override(underwater ? new Color(0.78f, 0.95f, 1f) : Color.white);
            sBloom.intensity.Override(0.45f + night * 0.25f + flash * 0.6f + golden * 0.1f);
            sBloom.threshold.Override(Mathf.Lerp(1.05f, 0.85f, night));
            sVignette.intensity.Override(0.18f + night * 0.08f + darkForest * 0.08f + (underwater ? 0.12f : 0f));
        }
    }
}
