using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Graphics harness for machines without a visible window (locked screen, CI): with -batchmode the
    /// player skips on-screen cameras, so this points the main camera at an offscreen target of the
    /// requested size every frame. That keeps the full URP frame (shadows, post, volumetrics) running for
    /// -bench, and -gfxshots t1,t2,... writes PNGs (gfx_NN_Ts.png in -shotdir) from that target, timed from
    /// the moment the area around the camera is streamed in. Quits after the last capture when
    /// -gfxquit is given. Windowed runs are untouched.
    /// </summary>
    public sealed class OffscreenCapture : MonoBehaviour
    {
        public static bool Wanted => Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null &&
                                     (Cli.Has("gfxshots") || Cli.Has("bench") || Cli.Has("offscreen"));

        RenderTexture mRT;
        int mW, mH;

        void Awake()
        {
            mW = (int)Cli.Float("screen-width", 1600);
            mH = (int)Cli.Float("screen-height", 900);
            mRT = new RenderTexture(mW, mH, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat_S8_UInt)
            {
                name = "WM Offscreen", antiAliasing = 1,
            };
            mRT.Create();
            Debug.Log($"WM_GFXCAP offscreen target {mW}x{mH}");
            if (Cli.Has("gfxshots")) StartCoroutine(Capture());
        }

        void OnDestroy() { if (mRT != null) mRT.Release(); }

        // Batchmode players skip camera rendering in the player loop, so render the main camera explicitly
        // (a full URP frame, including beginCameraRendering passes and post-processing) once per frame.
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.targetTexture != mRT) cam.targetTexture = mRT;
            var req = new UniversalRenderPipeline.SingleCameraRequest { destination = mRT };
            if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
            else cam.Render();
        }

        IEnumerator Capture()
        {
            var root = GameRoot.Instance;
            while (root == null || root.State != GameState.Playing) { root = GameRoot.Instance; yield return null; }
            // Let the harness place the camera (-camat/-camprop) and stream the view in.
            yield return new WaitForSecondsRealtime(1.5f);
            float t0 = Time.realtimeSinceStartup;
            var w = VoxelWorld.Instance;
            while (w != null && Camera.main != null && !w.ReadyAround(Camera.main.transform.position, Mathf.Min(6, w.ViewDistance)) &&
                   Time.realtimeSinceStartup - t0 < 60f) yield return null;

            string dir = Cli.Str("shotdir", Path.Combine(Application.persistentDataPath, "Screenshots"));
            Directory.CreateDirectory(dir);
            var times = Cli.Floats("gfxshots");
            if (times.Count == 0) times.Add(4f);
            float start = Time.realtimeSinceStartup;
            var tex = new Texture2D(mW, mH, TextureFormat.RGB24, false, false);
            int n = 0;
            foreach (var t in times)
            {
                while (Time.realtimeSinceStartup - start < t) yield return null;
                yield return new WaitForEndOfFrame();
                var prev = RenderTexture.active;
                RenderTexture.active = mRT;
                tex.ReadPixels(new Rect(0, 0, mW, mH), 0, 0, false);
                tex.Apply(false);
                RenderTexture.active = prev;
                string path = Path.Combine(dir, $"gfx_{n++:00}_{t:0}s.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                var cp = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                Debug.Log($"WM_GFXSHOT {path} cam={cp} time={(Atmosphere.Instance != null ? Atmosphere.Instance.TimeOfDay : 0):F3}");
            }
            Destroy(tex);
            if (Cli.Has("gfxquit")) Application.Quit(0);
        }
    }
}
