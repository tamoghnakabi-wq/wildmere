using UnityEngine;
using UnityEngine.Rendering;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Camera-relative rain, snow and splashes with zero per-particle CPU work: one static mesh of quads
    /// carrying random seeds, animated entirely in the vertex shader (Wildmere/WeatherFx) inside a box that
    /// wraps around the camera in world space (so drops don't swim when you move). Occlusion uses a small
    /// world-anchored height texture of the highest light-blocking block per column (Chunk.SkyTop), so
    /// nothing falls under roofs, trees or in caves, and splashes land exactly on the exposed surface
    /// (including water). Density follows GameSettings.WeatherParticles via RenderQuality.ParticleDensity.
    /// </summary>
    public sealed class WeatherParticles : MonoBehaviour
    {
        const int MaxRain = 9000, MaxSnow = 7000, MaxSplash = 700, MapSize = 64;

        Mesh mRainMesh, mSnowMesh, mSplashMesh;
        Material mRain, mSnow, mSplash;
        Texture2D mHeight;
        readonly float[] mHeights = new float[MapSize * MapSize];
        float mHeightTimer;
        float mRainI, mSnowI, mWind, mFlash;
        Vector2 mWindDir = Vector2.right;
        float mFallPhase, mSnowPhase;

        static readonly int kParams = Shader.PropertyToID("_WxParams");
        static readonly int kParams2 = Shader.PropertyToID("_WxParams2");
        static readonly int kHeight = Shader.PropertyToID("_WM_WxHeight");
        static readonly int kWxLight = Shader.PropertyToID("_WM_WxLight");
        static readonly int kWxWind = Shader.PropertyToID("_WM_WxWind");

        public void SetState(float rain, float snow, float wind, Vector2 windDir, float flash)
        {
            mRainI = rain; mSnowI = snow; mWind = wind; mWindDir = windDir; mFlash = flash;
        }

        void Awake()
        {
            var sh = Shader.Find("Wildmere/WeatherFx");
            if (sh == null) { Debug.LogWarning("WM_GFX Wildmere/WeatherFx missing; no weather particles"); enabled = false; return; }
            mRain = new Material(sh) { name = "Rain", enableInstancing = false };
            mRain.SetFloat("_Mode", 0);
            mSnow = new Material(sh) { name = "Snow", enableInstancing = false };
            mSnow.SetFloat("_Mode", 1);
            mSplash = new Material(sh) { name = "Splash", enableInstancing = false };
            mSplash.SetFloat("_Mode", 3);
            mRainMesh = BuildMesh(MaxRain, 11);
            mSnowMesh = BuildMesh(MaxSnow, 23);
            mSplashMesh = BuildMesh(MaxSplash, 37);
            mHeight = new Texture2D(MapSize, MapSize, TextureFormat.RFloat, false, true)
            {
                name = "WM WeatherHeight", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat,
            };
            Shader.SetGlobalTexture(kHeight, mHeight);
        }

        static Mesh BuildMesh(int quads, int seed)
        {
            var rng = new System.Random(seed);
            var verts = new Vector3[quads * 4];
            var uv0 = new Vector2[quads * 4];
            var uv1 = new Vector4[quads * 4];
            var idx = new int[quads * 6];
            for (int q = 0; q < quads; q++)
            {
                var s = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), q / (float)quads);
                for (int k = 0; k < 4; k++)
                {
                    int v = q * 4 + k;
                    uv0[v] = new Vector2(k == 1 || k == 2 ? 1 : 0, k >= 2 ? 1 : 0);
                    uv1[v] = s;
                }
                int i = q * 6, b = q * 4;
                idx[i] = b; idx[i + 1] = b + 2; idx[i + 2] = b + 1; idx[i + 3] = b; idx[i + 4] = b + 3; idx[i + 5] = b + 2;
            }
            var m = new Mesh { name = "WeatherQuads", indexFormat = IndexFormat.UInt32 };
            m.vertices = verts;
            m.uv = uv0;
            m.SetUVs(1, uv1);
            m.SetIndices(idx, MeshTopology.Triangles, 0, false);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            m.UploadMeshData(true);
            return m;
        }

        void RefreshHeights(Vector3 cam)
        {
            var w = VoxelWorld.Instance;
            int cx = Mathf.FloorToInt(cam.x), cz = Mathf.FloorToInt(cam.z);
            int x0 = cx - MapSize / 2, z0 = cz - MapSize / 2;
            for (int z = z0; z < z0 + MapSize; z++)
            {
                Chunk ch = null;
                int lastCx = int.MinValue;
                for (int x = x0; x < x0 + MapSize; x++)
                {
                    int ccx = x >> 4;
                    if (ccx != lastCx) { ch = w != null ? w.GetChunk(ccx, z >> 4) : null; lastCx = ccx; }
                    float h;
                    if (ch == null || ch.State != ChunkState.Generated) h = 255f;      // unknown: no rain there yet
                    else h = ch.SkyTop[(x & 15) + ((z & 15) << 4)] + 1f;
                    mHeights[(x & (MapSize - 1)) + (z & (MapSize - 1)) * MapSize] = h;
                }
            }
            mHeight.SetPixelData(mHeights, 0);
            mHeight.Apply(false);
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            bool any = mRainI > 0.01f || mSnowI > 0.01f;
            if (cam == null || !any || RenderQuality.ParticleDensity <= 0f) return;
            if (GameRoot.Instance != null && GameRoot.Instance.State == GameState.MainMenu) return;
            Vector3 c = cam.transform.position;
            mHeightTimer -= Time.deltaTime;
            if (mHeightTimer <= 0f) { mHeightTimer = 0.2f; RefreshHeights(c); }

            var atm = Atmosphere.Instance;
            Color amb = atm != null ? atm.AmbientSky : Color.grey;
            Color lc = atm != null ? atm.LightColor : Color.white;
            var light = new Color(amb.r * 0.9f + lc.r * 0.12f + mFlash * 1.5f, amb.g * 0.9f + lc.g * 0.12f + mFlash * 1.6f, amb.b * 0.9f + lc.b * 0.12f + mFlash * 2.0f, mFlash);
            Shader.SetGlobalColor(kWxLight, light);
            // Wind slant (horizontal metres per metre of fall) — gusty storms push the rain sideways.
            float slant = 0.08f + mWind * 0.22f;
            Shader.SetGlobalVector(kWxWind, new Vector4(mWindDir.x * slant, mWindDir.y * slant, mWind, 0));
            // Phases wrap hourly (one invisible re-shuffle) to keep float precision in the shader.
            mFallPhase = Mathf.Repeat(mFallPhase + Time.deltaTime, 3600f);
            mSnowPhase = Mathf.Repeat(mSnowPhase + Time.deltaTime, 3600f);
            float density = RenderQuality.ParticleDensity;
            var rp = new RenderParams
            {
                layer = Layers.Fx,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(c, Vector3.one * 200f),
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
            };
            if (mRainI > 0.01f)
            {
                int n = Mathf.RoundToInt(MaxRain * density * Mathf.Clamp01(mRainI * 1.15f));
                // x: active fraction, y: box half-size, z: box height, w: fall speed
                mRain.SetVector(kParams, new Vector4(n / (float)MaxRain, 15f, 22f, 11f));
                mRain.SetVector(kParams2, new Vector4(mFallPhase, Mathf.Clamp01(mRainI), 0.75f, 0.018f));
                rp.material = mRain;
                Graphics.RenderMesh(rp, mRainMesh, 0, Matrix4x4.identity);
                int ns = Mathf.RoundToInt(MaxSplash * density * Mathf.Clamp01(mRainI * 1.2f));
                mSplash.SetVector(kParams, new Vector4(ns / (float)MaxSplash, 13f, 0f, 0f));
                mSplash.SetVector(kParams2, new Vector4(mFallPhase, Mathf.Clamp01(mRainI), 0f, 0f));
                rp.material = mSplash;
                Graphics.RenderMesh(rp, mSplashMesh, 0, Matrix4x4.identity);
            }
            if (mSnowI > 0.01f)
            {
                int n = Mathf.RoundToInt(MaxSnow * density * Mathf.Clamp01(mSnowI * 1.15f));
                mSnow.SetVector(kParams, new Vector4(n / (float)MaxSnow, 20f, 24f, 1.3f));
                mSnow.SetVector(kParams2, new Vector4(mSnowPhase, Mathf.Clamp01(mSnowI), 0.06f, 0f));
                rp.material = mSnow;
                Graphics.RenderMesh(rp, mSnowMesh, 0, Matrix4x4.identity);
            }
        }

        void OnDestroy()
        {
            if (mRainMesh != null) Destroy(mRainMesh);
            if (mSnowMesh != null) Destroy(mSnowMesh);
            if (mSplashMesh != null) Destroy(mSplashMesh);
            if (mHeight != null) Destroy(mHeight);
        }
    }
}
