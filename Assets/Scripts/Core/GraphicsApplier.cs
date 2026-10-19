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
                ApplyRenderScale(urp, s);
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
            ApplyDisplay(s);
            Debug.Log($"WM_GFX preset={s.Preset} view={s.ViewDistance} scale={s.RenderScale} shadows={s.ShadowQuality}/{s.ShadowDistance} ssao={s.Ssao} aa={s.Antialiasing}");
        }

        // The 3D view renders at most about this many pixels at 100% render scale; the UI always uses the full
        // screen. Fullscreen on a MacBook Air is 2940x1846 backing pixels (more than the 2560-wide panel), which
        // would cost ~3.8x the old 1600x900 frame. Larger render scales still go past the budget.
        const float kPixelBudget = 1920f * 1200f;
        static int sScaleW, sScaleH;

        static void ApplyRenderScale(UniversalRenderPipelineAsset urp, GameSettings s)
        {
            sScaleW = Screen.width; sScaleH = Screen.height;
            float px = Mathf.Max(1, sScaleW) * (float)Mathf.Max(1, sScaleH);
            float fit = Mathf.Min(1f, Mathf.Sqrt(kPixelBudget / px));
            float scale = Mathf.Clamp(s.RenderScale * fit, 0.25f, 2f);
            if (Mathf.Abs(urp.renderScale - scale) < 0.001f) return;
            urp.renderScale = scale;
            Debug.Log($"WM_RES screen={sScaleW}x{sScaleH} render={Mathf.RoundToInt(sScaleW * scale)}x{Mathf.RoundToInt(sScaleH * scale)} scale={scale:0.###} setting={s.RenderScale:0.##}");
        }

        static int sDisplayMode = -1, sDisplayW, sDisplayH;

        /// <summary>
        /// Window mode. Fullscreen modes render at the display's own resolution: only switching the mode keeps
        /// the last window size (1600x900, 16:9), which the OS letterboxes on a 16:10 MacBook screen.
        /// Runs only when the mode or the display changes, never in batch runs or when Unity's -screen-* flags
        /// set the window explicitly (harness runs).
        /// </summary>
        static void ApplyDisplay(GameSettings s)
        {
            if (Application.isEditor || Application.isBatchMode || Cli.Has("screen-fullscreen") || Cli.Has("screen-width")) return;
            var di = Screen.mainWindowDisplayInfo;
            if (di.width <= 0 || di.height <= 0) return;
            if (s.Fullscreen == sDisplayMode && di.width == sDisplayW && di.height == sDisplayH) return;
            sDisplayMode = s.Fullscreen; sDisplayW = di.width; sDisplayH = di.height;
            if (s.Fullscreen == 0)
            {
                if (Screen.fullScreenMode != FullScreenMode.Windowed)
                {
                    // 16:9 at about three-quarters of the display (sizes are backing pixels, so 1600x900 was a
                    // small 800x450-point window on a Retina Mac).
                    int w = di.width * 3 / 4, h = w * 9 / 16;
                    if (h > di.height * 3 / 4) { h = di.height * 3 / 4; w = h * 16 / 9; }
                    Screen.SetResolution(w, h, FullScreenMode.Windowed);
                    Screen.MoveMainWindowTo(di, new Vector2Int((di.workArea.width - w) / 2, (di.workArea.height - h) / 2));
                }
            }
            else
            {
                var mode = s.Fullscreen == 2 ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.FullScreenWindow;
                Screen.SetResolution(di.width, di.height, mode);
            }
            Debug.Log($"WM_DISPLAY mode={s.Fullscreen} display={di.width}x{di.height} was={Screen.width}x{Screen.height} {Screen.fullScreenMode}");
        }

        float mDiagAt = -1f;
        int mCycle = -1;
        float mCycleAt;

        // -displaycycle: switch windowed -> borderless -> fullscreen (4 s each), logging the result of each switch.
        void DisplayCycle()
        {
            if (mCycle < 0) { if (!Cli.Has("displaycycle")) { mCycle = 99; return; } GameSettings.SessionOnly = true; mCycle = 0; mCycleAt = Time.realtimeSinceStartup + 4f; }
            if (mCycle >= 4 || Time.realtimeSinceStartup < mCycleAt) return;
            if (mCycle > 0) Debug.Log($"WM_DISPLAYCYCLE step={mCycle} setting={GameSettings.Current.Fullscreen} screen={Screen.width}x{Screen.height} mode={Screen.fullScreenMode} render={urpScale()}");
            if (mCycle < 3) { GameSettings.Current.Fullscreen = mCycle == 0 ? 0 : mCycle == 1 ? 1 : 2; Apply(); }
            mCycle++; mCycleAt = Time.realtimeSinceStartup + 4f;
            static string urpScale() => (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.renderScale.ToString("0.###");
        }

        void Update()
        {
            DisplayCycle();
            // Resolution changes (mode switch, window resize, display change) land a frame later: refit the 3D scale.
            if (Screen.width != sScaleW || Screen.height != sScaleH)
            {
                var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (urp != null) ApplyRenderScale(urp, GameSettings.Current);
            }
            // -displaydiag: log what Unity reports for the screen a few seconds after boot (fullscreen debugging).
            if (mDiagAt < 0f) mDiagAt = Cli.Has("displaydiag") ? Time.realtimeSinceStartup + 4f : float.MaxValue;
            if (Time.realtimeSinceStartup < mDiagAt) return;
            mDiagAt = float.MaxValue;
            var di = Screen.mainWindowDisplayInfo;
            var d = Display.main;
            Debug.Log($"WM_DISPLAYDIAG screen={Screen.width}x{Screen.height} mode={Screen.fullScreenMode} current={Screen.currentResolution.width}x{Screen.currentResolution.height} " +
                      $"info={di.width}x{di.height} work={di.workArea} system={d.systemWidth}x{d.systemHeight} rendering={d.renderingWidth}x{d.renderingHeight} " +
                      $"safe={Screen.safeArea} cutouts={Screen.cutouts.Length} dpi={Screen.dpi}");
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
