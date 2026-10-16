using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Wildmere.Core;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Global post-processing volume built at runtime (baseline by the lead; Graphics owns tuning):
    /// ACES tonemapping, bloom, gentle colour grading, white balance, vignette.
    /// </summary>
    public static class PostFx
    {
        static Volume sVolume;
        static Bloom sBloom;
        static ColorAdjustments sColor;
        static Tonemapping sTone;
        static Vignette sVignette;
        static WhiteBalance sWhite;

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
            sBloom.threshold.Override(1.0f);
            sBloom.intensity.Override(0.55f);
            sBloom.scatter.Override(0.68f);
            sBloom.highQualityFiltering.Override(false);
            sColor = p.Add<ColorAdjustments>(true);
            sColor.postExposure.Override(0.15f);
            sColor.contrast.Override(8f);
            sColor.saturation.Override(6f);
            sWhite = p.Add<WhiteBalance>(true);
            sWhite.temperature.Override(4f);
            sVignette = p.Add<Vignette>(true);
            sVignette.intensity.Override(0.22f);
            sVignette.smoothness.Override(0.45f);
        }

        public static void Apply(GameSettings s)
        {
            Ensure();
            sBloom.active = s.Bloom;
            sColor.postExposure.Override(0.15f + s.Brightness * 0.8f);
        }
    }
}
