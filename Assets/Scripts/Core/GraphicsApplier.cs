using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Wildmere.World;

namespace Wildmere.Core
{
    /// <summary>
    /// Pushes <see cref="GameSettings"/> graphics values into URP, the camera and the world. Called at
    /// boot, whenever settings change, and after a world loads.
    /// </summary>
    public sealed class GraphicsApplier : MonoBehaviour
    {
        static readonly FieldInfo kRendererList = typeof(UniversalRenderPipelineAsset)
            .GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);

        void OnEnable() { GameSettings.Changed += Apply; }
        void OnDisable() { GameSettings.Changed -= Apply; }

        void Start()
        {
            if (GameRoot.Instance != null) GameRoot.Instance.WorldReady += Apply;
        }

        public static void Apply()
        {
            var s = GameSettings.Current;
            QualitySettings.SetQualityLevel(Mathf.Clamp((int)(s.Preset == GraphicsPreset.Custom ? GraphicsPreset.High : s.Preset), 0, 3), false);
            QualitySettings.vSyncCount = s.VSync ? 1 : 0;
            Application.targetFrameRate = s.FpsCap > 0 ? s.FpsCap : (s.VSync ? -1 : (Cli.Has("novsync") ? 1000 : 240));

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                urp.renderScale = Mathf.Clamp(s.RenderScale, 0.5f, 2f);
                urp.shadowDistance = s.ShadowQuality == 0 ? 0f : s.ShadowDistance;
                urp.shadowCascadeCount = s.ShadowQuality >= 2 ? 4 : 2;
                urp.mainLightShadowmapResolution = s.ShadowQuality >= 3 ? 4096 : s.ShadowQuality == 2 ? 2048 : 1024;
                urp.msaaSampleCount = s.Antialiasing == 3 ? 4 : s.Antialiasing == 2 ? 2 : 1;
                SetSsao(urp, s.Ssao);
            }

            var light = RenderSettings.sun;
            if (light != null) light.shadows = s.ShadowQuality == 0 ? LightShadows.None : LightShadows.Soft;

            var cam = Camera.main;
            if (cam != null)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = s.Antialiasing == 1 ? AntialiasingMode.FastApproximateAntialiasing : AntialiasingMode.None;
                data.renderShadows = s.ShadowQuality > 0;
                cam.allowMSAA = s.Antialiasing >= 2;
                cam.allowHDR = true;
            }

            var w = VoxelWorld.Instance;
            if (w != null)
            {
                w.ViewDistance = Mathf.Clamp(s.ViewDistance, 4, 20);
                w.FancyLeaves = s.FancyLeaves;
                if (cam != null) cam.farClipPlane = Mathf.Max(300f, w.ViewDistance * 16f + 200f);
            }
            Rendering.PostFx.Apply(s);
            Screen.fullScreenMode = s.Fullscreen == 2 ? FullScreenMode.ExclusiveFullScreen : s.Fullscreen == 1 ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            Debug.Log($"WM_GFX preset={s.Preset} view={s.ViewDistance} scale={s.RenderScale} shadows={s.ShadowQuality}/{s.ShadowDistance} ssao={s.Ssao} aa={s.Antialiasing}");
        }

        static void SetSsao(UniversalRenderPipelineAsset urp, bool on)
        {
            if (kRendererList == null) return;
            if (!(kRendererList.GetValue(urp) is ScriptableRendererData[] list)) return;
            foreach (var rd in list)
            {
                if (rd == null) continue;
                foreach (var f in rd.rendererFeatures)
                    if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion") f.SetActive(on);
            }
        }
    }
}
