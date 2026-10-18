using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Wildmere.Core;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Per-preset switches for the graphics module (clouds, volumetric light, water refraction, caustics,
    /// puddles, weather particle density). Applied from <see cref="PostFx.Apply"/>, i.e. whenever
    /// GraphicsApplier pushes settings. Custom maps to High, then the explicit GameSettings toggles
    /// (Clouds, WaterReflections, WeatherParticles) override.
    /// </summary>
    public static class RenderQuality
    {
        public static GraphicsPreset Level { get; private set; } = GraphicsPreset.High;
        public static int CloudSteps = 6;
        public static bool CloudShadows = true, Caustics = true, Puddles = true, DistanceDetailFade = true;
        /// <summary>Water refracts the opaque scene texture (otherwise alpha-blended).</summary>
        public static bool Refraction = true;
        /// <summary>Shadow-mapped volumetric sun light: 0 off, 1 low, 2 high, 3 ultra.</summary>
        public static int Volumetric = 2;
        /// <summary>Weather particle budget multiplier (0.25 .. 1).</summary>
        public static float ParticleDensity = 1f;

        static readonly int kQuality = Shader.PropertyToID("_WM_Quality");
        static bool sInstalled;

        public static void Apply(GameSettings s)
        {
            Level = s.Preset == GraphicsPreset.Custom ? GraphicsPreset.High : s.Preset;
            switch (Level)
            {
                case GraphicsPreset.Low:
                    CloudSteps = 2; CloudShadows = false; Caustics = false; Puddles = false; Refraction = false; Volumetric = 0; break;
                case GraphicsPreset.Medium:
                    CloudSteps = 4; CloudShadows = true; Caustics = true; Puddles = true; Refraction = true; Volumetric = 1; break;
                case GraphicsPreset.High:
                    CloudSteps = 6; CloudShadows = true; Caustics = true; Puddles = true; Refraction = true; Volumetric = 2; break;
                default:
                    CloudSteps = 8; CloudShadows = true; Caustics = true; Puddles = true; Refraction = true; Volumetric = 3; break;
            }
            if (!s.Clouds) { CloudSteps = 1; CloudShadows = false; }
            if (!s.WaterReflections) Refraction = false;
            if (s.ShadowQuality == 0) Volumetric = 0;   // volumetrics march the shadow map
            if (Cli.Has("novolumetric")) Volumetric = 0;
            ParticleDensity = s.WeatherParticles <= 0 ? 0.3f : s.WeatherParticles == 1 ? 0.6f : 1f;
            Shader.SetGlobalVector(kQuality, new Vector4(Caustics ? 1 : 0, CloudShadows ? 1 : 0, Puddles ? 1 : 0, DistanceDetailFade ? 1 : 0));
            if (Refraction) Shader.EnableKeyword("WM_REFRACTION"); else Shader.DisableKeyword("WM_REFRACTION");

            // Shadows: tighter first cascades for crisp contact shadows near the player, a little more depth
            // bias at the cascade sizes the voxel world uses (no acne on big flat faces at low sun), small
            // normal bias (no peter-panning under blocks).
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.shadowDepthBias = 0.75f;
                urp.shadowNormalBias = 0.55f;
                if (urp.shadowCascadeCount == 4) urp.cascade4Split = new Vector3(0.06f, 0.18f, 0.42f);
                else if (urp.shadowCascadeCount == 2) urp.cascade2Split = 0.3f;
            }
            Debug.Log($"WM_GFXQ level={Level} clouds={CloudSteps} vol={Volumetric} refraction={Refraction} caustics={Caustics} particles={ParticleDensity}");
        }

        /// <summary>Hook camera callbacks once (called by Atmosphere).</summary>
        public static void Install()
        {
            if (sInstalled) return;
            sInstalled = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam.cameraType != CameraType.Game) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return;
            var want = Refraction ? CameraOverrideOption.On : CameraOverrideOption.Off;
            if (data.requiresColorOption != want) data.requiresColorOption = want;
            VolumetricLight.Enqueue(cam, data);
        }
    }
}
