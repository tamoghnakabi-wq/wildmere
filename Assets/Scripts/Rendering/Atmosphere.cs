using UnityEngine;
using UnityEngine.Rendering;
using Wildmere.World;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Day/night cycle and every global the world shaders read. Time of day is 0..1 (0 = midnight,
    /// 0.25 = sunrise, 0.5 = noon, 0.75 = sunset). One directional light is reused for the moon at night so
    /// there is only ever one shadow-casting main light.
    ///
    /// Colour model: the sun's colour comes from atmospheric transmittance over the air mass (so low sun is
    /// naturally golden, then red), the sky ambient is a desaturated average of the dome (no cyan cast on
    /// grass), haze is an ambient in-scatter colour plus a sun-scattered lobe, and weather/biome inputs
    /// (cloud cover, rain, fog, dark forest, morning mist) modulate all of it with smooth transitions.
    /// </summary>
    public sealed class Atmosphere : MonoBehaviour
    {
        public static Atmosphere Instance { get; private set; }

        [Range(0, 1)] public float TimeOfDay = 0.30f;
        public float DayLengthSeconds = 1200f;   // full cycle (20 minutes)
        public bool Paused;
        public float Overcast;                   // 0..1 cloud cover (set by Weather)
        public float Rain;                       // 0..1 precipitation (rain or snow, set by Weather)
        public float Snow;                       // 0..1 share of precipitation falling as snow
        public float Storm;                      // 0..1 thunderstorm darkness
        public float FogBoost;                   // 0..1 weather fog
        public float WindStrength = 0.8f;        // set by Weather
        public Vector2 WindDir = new Vector2(0.8f, 0.6f);
        public float ViewDistanceMeters = 160f;
        public Light Sun { get; private set; }
        public Material SkyMaterial { get; private set; }
        public bool Underwater;

        /// <summary>Current dark-forest factor around the camera (0..1, smoothed).</summary>
        public float Darkness { get; private set; }
        /// <summary>Current ground-mist density factor (0..1, smoothed).</summary>
        public float MistAmount { get; private set; }
        /// <summary>0 night .. 1 day.</summary>
        public float DayFactor { get; private set; }
        /// <summary>Warm low-sun factor (golden hour / sunrise), 0..1.</summary>
        public float Golden { get; private set; }
        /// <summary>Lightning flash 0..1 (set by Weather each frame).</summary>
        public float Flash;
        public Vector3 FlashDir = Vector3.up;
        /// <summary>Linear colour * intensity of the main light this frame (for volumetrics/particles).</summary>
        public Color LightColor { get; private set; }
        public Color AmbientSky { get; private set; }
        public Color FogColor { get; private set; }

        static readonly int kSunDir = Shader.PropertyToID("_WM_SunDir"), kRealSunDir = Shader.PropertyToID("_WM_RealSunDir"),
            kMoonDir = Shader.PropertyToID("_WM_MoonDir"), kAmbSky = Shader.PropertyToID("_WM_AmbientSky"),
            kAmbGround = Shader.PropertyToID("_WM_AmbientGround"), kCave = Shader.PropertyToID("_WM_CaveAmbient"),
            kBlock = Shader.PropertyToID("_WM_BlockLight"), kFogCol = Shader.PropertyToID("_WM_FogColor"),
            kFogSun = Shader.PropertyToID("_WM_FogSunColor"), kFog = Shader.PropertyToID("_WM_Fog"),
            kMist = Shader.PropertyToID("_WM_Mist"),
            kFade = Shader.PropertyToID("_WM_ViewFade"), kWind = Shader.PropertyToID("_WM_Wind"),
            kWindOff = Shader.PropertyToID("_WM_WindOffset"),
            kWet = Shader.PropertyToID("_WM_Wetness"), kUnder = Shader.PropertyToID("_WM_Underwater"),
            kUnderCol = Shader.PropertyToID("_WM_UnderwaterColor"), kZenith = Shader.PropertyToID("_WM_SkyZenith"),
            kHorizon = Shader.PropertyToID("_WM_SkyHorizon"), kSunCol = Shader.PropertyToID("_WM_SunColor"),
            kClouds = Shader.PropertyToID("_WM_Clouds"), kCloudLight = Shader.PropertyToID("_WM_CloudLight"),
            kCloudAmb = Shader.PropertyToID("_WM_CloudAmbient"), kStars = Shader.PropertyToID("_WM_Stars"),
            kPrecip = Shader.PropertyToID("_WM_Precip"), kLightning = Shader.PropertyToID("_WM_Lightning"),
            kCloudNoise = Shader.PropertyToID("_WM_CloudNoise"), kCaustics = Shader.PropertyToID("_WM_Caustics"),
            kLightCol = Shader.PropertyToID("_WM_LightColor"), kSurface = Shader.PropertyToID("_WM_Surface");

        // Sky colours keyed by sun height (sin of elevation), authored in sRGB.
        static readonly (float h, Color zen, Color hor)[] kSky =
        {
            (-0.30f, C("05070f"), C("0b1020")),
            (-0.14f, C("0a1024"), C("172036")),
            (-0.06f, C("1a2648"), C("3a3f5c")),
            ( 0.00f, C("324f86"), C("c98e78")),
            ( 0.06f, C("3f67a8"), C("e8b48e")),
            ( 0.16f, C("4677bd"), C("e4cdb2")),
            ( 0.32f, C("3d77c9"), C("bcd3e8")),
            ( 0.60f, C("2f6ac4"), C("b4cfe9")),
            ( 1.00f, C("2a63bf"), C("b0cdea")),
        };

        float mWetness, mPuddles, mBiomeTimer;
        /// <summary>Accumulated snow cover 0..1 (grows while snowing, melts slowly otherwise).</summary>
        public float SnowCover;
        float mTargetDarkness, mTargetForest, mGroundY = 64f;
        Vector2 mCloudOffset;
        float mWaveTime;

        static Color C(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out var c); return c; }

        void Awake()
        {
            Instance = this;
            var go = new GameObject("Sun");
            go.transform.SetParent(transform, false);
            Sun = go.AddComponent<Light>();
            Sun.type = LightType.Directional;
            Sun.shadows = LightShadows.Soft;
            Sun.shadowStrength = 1f;
            Sun.shadowBias = 0.06f;
            Sun.shadowNormalBias = 0.5f;
            RenderSettings.sun = Sun;

            var sky = Shader.Find("Wildmere/Sky");
            if (sky != null)
            {
                SkyMaterial = new Material(sky) { name = "Sky" };
                RenderSettings.skybox = SkyMaterial;
            }
            var noise = Resources.Load<Texture2D>("Textures/cloud_noise");
            if (noise != null) Shader.SetGlobalTexture(kCloudNoise, noise);
            Shader.SetGlobalTexture(kCaustics, CausticsTexture.Get());
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = false;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            mCloudOffset = new Vector2(Random.Range(0f, 5000f), Random.Range(0f, 5000f));
            if (GetComponent<Weather>() == null) gameObject.AddComponent<Weather>();
            if (OffscreenCapture.Wanted) gameObject.AddComponent<OffscreenCapture>();
            if (GfxHarness.Wanted) gameObject.AddComponent<GfxHarness>();
            RenderQuality.Install();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            if (!Paused && DayLengthSeconds > 0) TimeOfDay = Mathf.Repeat(TimeOfDay + Time.deltaTime / DayLengthSeconds, 1f);
            SampleBiome();
            Apply();
        }

        /// <summary>Direction towards the sun for a time of day (sun rises in +X, sets in -X, tilted south).</summary>
        public static Vector3 SunDirection(float t)
        {
            float a = (t - 0.25f) * Mathf.PI * 2f;
            var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            return Quaternion.AngleAxis(-28f, Vector3.right) * d;
        }

        // Atmospheric transmittance for a sun at height h (sin of elevation): per-channel extinction over the
        // Kasten-Young air mass. Returns linear RGB, not normalised.
        static Vector3 SunTransmittance(float h)
        {
            float e = Mathf.Asin(Mathf.Clamp(h, -1f, 1f)) * Mathf.Rad2Deg;
            float eC = Mathf.Max(e, -1.5f);
            float m = 1f / (Mathf.Sin(Mathf.Max(eC, 0.01f) * Mathf.Deg2Rad) + 0.50572f * Mathf.Pow(eC + 6.07995f, -1.6364f));
            var beta = new Vector3(0.032f, 0.085f, 0.19f);
            return new Vector3(Mathf.Exp(-beta.x * m), Mathf.Exp(-beta.y * m), Mathf.Exp(-beta.z * m));
        }

        void SampleBiome()
        {
            var w = VoxelWorld.Instance;
            var cam = Camera.main;
            if (w != null && w.Gen != null && cam != null)
            {
                mBiomeTimer -= Time.deltaTime;
                if (mBiomeTimer <= 0f)
                {
                    mBiomeTimer = 0.5f;
                    var p = cam.transform.position;
                    // Average a few samples so biome borders fade instead of snapping.
                    float dark = 0, forest = 0, hgt = 0;
                    for (int k = 0; k < 5; k++)
                    {
                        int ox = k == 1 ? 12 : k == 2 ? -12 : 0, oz = k == 3 ? 12 : k == 4 ? -12 : 0;
                        var s = w.Gen.SampleBiome(Mathf.FloorToInt(p.x) + ox, Mathf.FloorToInt(p.z) + oz);
                        dark += s.Biome == Biome.DarkForest ? Mathf.Max(0.75f, s.Darkness) : s.Darkness;
                        forest += Mathf.Max(s.Forest, s.Biome == Biome.TemperateForest || s.Biome == Biome.DarkForest ? 0.6f : 0f);
                        hgt += s.Height;
                    }
                    mTargetDarkness = Mathf.Clamp01(dark / 5f);
                    mTargetForest = Mathf.Clamp01(forest / 5f);
                    mGroundY = hgt / 5f;
                    int sy = w.SurfaceY(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
                    if (sy > 0) mGroundY = Mathf.Min(mGroundY, sy);
                }
            }
            float k2 = 1f - Mathf.Exp(-Time.deltaTime / 4f);
            Darkness = Mathf.Lerp(Darkness, mTargetDarkness, k2);
        }

        public void Apply()
        {
            float dt = Time.deltaTime;
            Vector3 sunDir = SunDirection(TimeOfDay).normalized;
            Vector3 moonDir = -sunDir;
            float sunH = sunDir.y;
            float day = Mathf.Clamp01((sunH + 0.08f) / 0.3f);                                        // 0 night .. 1 day
            float golden = Mathf.Clamp01(1f - Mathf.Abs(sunH - 0.13f) / 0.2f) * Mathf.Clamp01((sunH + 0.05f) / 0.08f);
            float cover = Mathf.Clamp01(Overcast);
            float overcast = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 0.95f, cover));          // uniform dimming
            float storm = Mathf.Clamp01(Storm);
            float dark = Darkness;
            DayFactor = day; Golden = golden;

            // ---------------------------------------------------------------- main light
            Vector3 T = SunTransmittance(sunH);
            float tMax = Mathf.Max(T.x, Mathf.Max(T.y, T.z));
            var sunCol = new Color(T.x / tMax, T.y / tMax, T.z / tMax);
            // Golden hour: extra warmth beyond plain extinction (aerosols, forward-scattered glow).
            sunCol *= Color.Lerp(Color.white, new Color(1f, 0.86f, 0.66f), golden);
            float sunLum = 0.2126f * T.x + 0.7152f * T.y + 0.0722f * T.z;
            float horizonFade = Mathf.Clamp01((sunH + 0.035f) / 0.09f);
            float sunI = 3.1f * Mathf.Pow(sunLum, 0.55f) * (1f + golden * 0.45f) * horizonFade * (1f - overcast * 0.82f) * (1f - storm * 0.5f);
            var moonCol = new Color(0.56f, 0.68f, 1.0f);
            float moonI = 0.42f * Mathf.Clamp01(moonDir.y * 3.5f) * (1f - overcast * 0.75f);

            bool useSun = sunH > -0.05f;
            Vector3 lightDir = useSun ? sunDir : moonDir;
            if (lightDir.y < 0.08f) lightDir = new Vector3(lightDir.x, 0.08f, lightDir.z).normalized;
            Sun.transform.rotation = Quaternion.LookRotation(-lightDir);
            Sun.color = useSun ? sunCol : moonCol;
            Sun.intensity = useSun ? sunI : moonI;
            Sun.shadowStrength = Mathf.Lerp(useSun ? 1f : 0.75f, 0.45f, overcast);
            LightColor = (useSun ? sunCol : moonCol) * (useSun ? sunI : moonI);

            // ---------------------------------------------------------------- sky
            EvalSky(sunH, out Color zenS, out Color horS);
            Color zen = zenS.linear, hor = horS.linear;
            float greyLum = Mathf.Lerp(0.004f, 0.42f, day) * (1f - storm * 0.55f);
            Color grey = new Color(0.82f, 0.86f, 0.92f) * greyLum;
            zen = Color.Lerp(zen, grey * 0.8f, overcast * 0.9f);
            hor = Color.Lerp(hor, grey * 1.05f, overcast * 0.85f);

            // ---------------------------------------------------------------- ambient
            Color avg = zen * 0.45f + hor * 0.55f;
            float avgLum = avg.r * 0.2126f + avg.g * 0.7152f + avg.b * 0.0722f;
            Color ambSky = Color.Lerp(new Color(avgLum, avgLum, avgLum), avg, 0.55f) * 1.35f;
            // Night: moonlit, cool, never pitch black.
            Color nightAmb = new Color(0.036f, 0.046f, 0.084f) * (1f - overcast * 0.4f);
            ambSky = new Color(Mathf.Max(ambSky.r, nightAmb.r), Mathf.Max(ambSky.g, nightAmb.g), Mathf.Max(ambSky.b, nightAmb.b));
            // Golden hour: the sky dome is still blue — cool shadows against the warm sun.
            ambSky *= Mathf.Lerp(1f, 0.66f, golden);
            Color bounce = new Color(0.34f, 0.30f, 0.22f) * (useSun ? sunI : moonI * 0.6f) * 0.055f;
            Color ambGround = ambSky * 0.32f + bounce;
            // Dark forest: the canopy eats the sky light.
            ambSky *= Mathf.Lerp(1f, 0.55f, dark);
            ambGround *= Mathf.Lerp(1f, 0.6f, dark);
            AmbientSky = ambSky;

            // ---------------------------------------------------------------- haze + mist
            Color fog = Color.Lerp(hor, zen, 0.22f) * Mathf.Lerp(1f, 0.85f, overcast);
            fog = Color.Lerp(fog, new Color(0.10f, 0.13f, 0.11f) * Mathf.Max(day, 0.1f), dark * 0.55f);
            // Sun-scattered haze: much stronger when the light crosses more air (sunrise/sunset).
            float sunScatter = (0.05f + golden * 0.16f) * (1f - overcast * 0.85f);
            Color fogSun = (useSun ? sunCol * sunI : moonCol * moonI * 0.5f) * sunScatter;
            // Volumetric light carries the shadowed part of the sun scatter when it is on.
            if (RenderQuality.Volumetric > 0) fogSun *= 0.7f;
            FogColor = fog;
            float density = 0.0022f + overcast * 0.0025f + Rain * 0.006f + FogBoost * 0.022f + dark * 0.010f;

            // Morning mist in forests/valleys (and any time in dark forests or fog weather).
            float morning = Mathf.Clamp01(1f - Mathf.Abs(TimeOfDay - 0.27f) / 0.07f);
            float mistTarget = Mathf.Clamp01(morning * (0.35f + mTargetForest * 0.65f) + dark * 0.55f + FogBoost * 0.7f + Rain * 0.15f);
            MistAmount = Mathf.MoveTowards(MistAmount, mistTarget, dt / 6f);
            float mistDensity = MistAmount * 0.022f;
            float mistBase = mGroundY - 1f;

            // ---------------------------------------------------------------- clouds
            float cloudAlt = 190f, cloudThick = Mathf.Lerp(110f, 260f, cover);
            Vector2 wind = WindDir.sqrMagnitude > 0.001f ? WindDir.normalized : Vector2.right;
            float cloudSpeed = 5f + WindStrength * 9f;
            mCloudOffset += wind * cloudSpeed * dt;
            if (mCloudOffset.x > 100000f || mCloudOffset.y > 100000f) mCloudOffset = Vector2.zero;
            mWaveTime += dt * (0.6f + WindStrength * 0.4f);
            // Cloud tops see the sun earlier/longer and less reddened than the ground.
            Vector3 Tc = SunTransmittance(sunH + 0.04f);
            float tcMax = Mathf.Max(Tc.x, Mathf.Max(Tc.y, Tc.z));
            float sunUp = Mathf.Clamp01((sunH + 0.06f) / 0.1f);
            Color cloudSun = new Color(Tc.x / tcMax, Tc.y / tcMax, Tc.z / tcMax) * (3.2f * Mathf.Pow(Mathf.Max(Tc.y, 0.02f), 0.4f) * sunUp);
            Color cloudMoon = moonCol * 0.07f * Mathf.Clamp01(moonDir.y * 3f);
            Color cloudLight = (sunUp > 0.01f ? cloudSun : cloudMoon) * (1f - storm * 0.6f);
            Color cloudAmb = (zen * 0.6f + hor * 0.5f) * 1.1f;
            cloudAmb = new Color(Mathf.Max(cloudAmb.r, 0.010f), Mathf.Max(cloudAmb.g, 0.012f), Mathf.Max(cloudAmb.b, 0.02f));
            cloudAmb *= 1f - storm * 0.5f;
            float cloudShadow = Mathf.Lerp(0.55f, 0.15f, overcast) * day;

            // ---------------------------------------------------------------- weather surface state
            float wetTarget = Rain > 0.15f && Snow < 0.5f ? 1f : 0f;
            mWetness = Mathf.MoveTowards(mWetness, wetTarget, dt / (wetTarget > mWetness ? 30f : 120f));
            mPuddles = Mathf.MoveTowards(mPuddles, wetTarget * Mathf.Clamp01(Rain * 1.3f), dt / (wetTarget > 0 ? 90f : 200f));
            float snowing = Rain * Snow;
            SnowCover = Mathf.MoveTowards(SnowCover, snowing > 0.2f ? 1f : 0f, dt / (snowing > 0.2f ? 120f : 400f));

            // ---------------------------------------------------------------- push globals
            Shader.SetGlobalVector(kSunDir, lightDir);
            Shader.SetGlobalVector(kRealSunDir, sunDir);
            Shader.SetGlobalVector(kMoonDir, new Vector4(moonDir.x, moonDir.y, moonDir.z, Mathf.Clamp01(moonDir.y * 5f + 0.2f) * (1f - cover * 0.8f)));
            Shader.SetGlobalColor(kAmbSky, ambSky);
            Shader.SetGlobalColor(kAmbGround, ambGround);
            Shader.SetGlobalColor(kCave, new Color(0.010f, 0.010f, 0.014f));
            Shader.SetGlobalVector(kBlock, new Vector4(1.0f, 0.56f, 0.26f, 3.2f));
            Shader.SetGlobalColor(kZenith, zen);
            Shader.SetGlobalVector(kHorizon, new Vector4(hor.r, hor.g, hor.b, 1f + golden * 2.5f));
            float sunVis = Mathf.Clamp01(sunH * 10f + 0.5f) * (1f - overcast * 0.95f);
            Shader.SetGlobalVector(kSunCol, new Vector4(sunCol.r * 1.25f, sunCol.g * 1.1f, sunCol.b, sunVis));
            Shader.SetGlobalFloat(kStars, Mathf.Clamp01(-sunH * 6f - 0.1f) * (1f - cover * 0.9f));
            Shader.SetGlobalVector(kClouds, new Vector4(cover, storm, cloudAlt, cloudThick));
            Shader.SetGlobalVector(kCloudLight, new Vector4(cloudLight.r, cloudLight.g, cloudLight.b, cloudShadow));
            Shader.SetGlobalVector(kCloudAmb, new Vector4(cloudAmb.r, cloudAmb.g, cloudAmb.b, RenderQuality.CloudSteps));
            Shader.SetGlobalColor(kFogCol, fog);
            Shader.SetGlobalColor(kFogSun, fogSun);
            Shader.SetGlobalVector(kFog, new Vector4(density, 0.035f, 62f, 12f));
            Shader.SetGlobalVector(kMist, new Vector4(mistDensity, 0.16f, mistBase, 0.5f + golden * 0.5f));
            Shader.SetGlobalVector(kFade, new Vector4(ViewDistanceMeters * 0.72f, ViewDistanceMeters * 0.97f, 0, 0));
            float gust = 0.35f + WindStrength * 0.35f;
            Shader.SetGlobalVector(kWind, new Vector4(wind.x, wind.y, WindStrength, gust));
            Shader.SetGlobalVector(kWindOff, new Vector4(mCloudOffset.x, mCloudOffset.y, mWaveTime, 0));
            Shader.SetGlobalFloat(kWet, mWetness);
            Shader.SetGlobalVector(kPrecip, new Vector4(Rain * (1f - Snow), Rain * Snow, mPuddles, Flash));
            Shader.SetGlobalVector(kLightning, new Vector4(FlashDir.x, FlashDir.y, FlashDir.z, Flash));
            Shader.SetGlobalFloat(kUnder, Underwater ? 1f : 0f);
            Shader.SetGlobalColor(kUnderCol, new Color(0.012f, 0.06f, 0.075f) * Mathf.Lerp(0.12f, 1f, day));
            Shader.SetGlobalColor(kLightCol, LightColor);
            Shader.SetGlobalVector(kSurface, new Vector4(SnowCover, Mathf.Clamp01(FogBoost), 0, 0));

            RenderSettings.ambientSkyColor = ambSky;
            RenderSettings.ambientEquatorColor = Color.Lerp(ambSky, ambGround, 0.5f);
            RenderSettings.ambientGroundColor = ambGround;

            // Volumetric light: strongest at golden hour, in morning mist and in dark forests; faint at noon.
            VolumetricLight.Strength = (0.55f + golden * 0.9f + dark * 0.9f + MistAmount * 0.5f + (useSun ? 0f : 0.6f)) * (1f - overcast * 0.75f);
            VolumetricLight.DensityBoost = 1f + dark * 2.5f + FogBoost * 1.5f + golden * 0.6f + Rain * 0.8f;
            VolumetricLight.Tint = Color.Lerp(Color.white, new Color(1f, 0.9f, 0.75f), golden);
            PostFx.UpdateFrame(day, golden, overcast, storm, dark, Underwater, Flash, sunH);
        }

        static void EvalSky(float h, out Color zen, out Color hor)
        {
            if (h <= kSky[0].h) { zen = kSky[0].zen; hor = kSky[0].hor; return; }
            for (int i = 1; i < kSky.Length; i++)
            {
                if (h <= kSky[i].h)
                {
                    float t = Mathf.InverseLerp(kSky[i - 1].h, kSky[i].h, h);
                    t = t * t * (3f - 2f * t);
                    zen = Color.Lerp(kSky[i - 1].zen, kSky[i].zen, t);
                    hor = Color.Lerp(kSky[i - 1].hor, kSky[i].hor, t);
                    return;
                }
            }
            zen = kSky[kSky.Length - 1].zen; hor = kSky[kSky.Length - 1].hor;
        }
    }
}
