using UnityEngine;
using UnityEngine.Rendering;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Day/night cycle and every global the world shaders read (baseline by the lead; the Graphics
    /// module owns and extends this). Time of day is 0..1 (0 = midnight, 0.25 = sunrise, 0.5 = noon,
    /// 0.75 = sunset). One directional light is reused for the moon at night so there is only ever
    /// one shadow-casting main light.
    /// </summary>
    public sealed class Atmosphere : MonoBehaviour
    {
        public static Atmosphere Instance { get; private set; }

        [Range(0, 1)] public float TimeOfDay = 0.30f;
        public float DayLengthSeconds = 1200f;   // full cycle (20 minutes)
        public bool Paused;
        public float Overcast;                   // 0..1, set by weather
        public float Rain;                       // 0..1, set by weather
        public float FogBoost;                   // extra fog from biome/weather
        public float ViewDistanceMeters = 160f;
        public Light Sun { get; private set; }
        public Material SkyMaterial { get; private set; }
        public bool Underwater;

        static readonly int kSunDir = Shader.PropertyToID("_WM_SunDir"), kRealSunDir = Shader.PropertyToID("_WM_RealSunDir"),
            kMoonDir = Shader.PropertyToID("_WM_MoonDir"), kAmbSky = Shader.PropertyToID("_WM_AmbientSky"),
            kAmbGround = Shader.PropertyToID("_WM_AmbientGround"), kCave = Shader.PropertyToID("_WM_CaveAmbient"),
            kBlock = Shader.PropertyToID("_WM_BlockLight"), kFogCol = Shader.PropertyToID("_WM_FogColor"),
            kFogSun = Shader.PropertyToID("_WM_FogSunColor"), kFog = Shader.PropertyToID("_WM_Fog"),
            kFade = Shader.PropertyToID("_WM_ViewFade"), kWind = Shader.PropertyToID("_WM_Wind"),
            kWet = Shader.PropertyToID("_WM_Wetness"), kUnder = Shader.PropertyToID("_WM_Underwater"),
            kUnderCol = Shader.PropertyToID("_WM_UnderwaterColor"), kZenith = Shader.PropertyToID("_WM_SkyZenith"),
            kHorizon = Shader.PropertyToID("_WM_SkyHorizon"), kSunCol = Shader.PropertyToID("_WM_SunColor"),
            kClouds = Shader.PropertyToID("_WM_Clouds"), kStars = Shader.PropertyToID("_WM_Stars");

        // Key colours over the day (linear). Index by sun elevation bands.
        static readonly Gradient sZenith = G(("1f2747", 0f), ("33508a", 0.42f), ("3f6fb8", 0.5f), ("4a82cc", 0.62f), ("5a93d6", 1f));
        static readonly Gradient sHorizon = G(("2a3048", 0f), ("c9805a", 0.44f), ("e7b48c", 0.5f), ("b8cde0", 0.6f), ("cfe0ee", 1f));

        float mWetness;

        static Gradient G(params (string hex, float t)[] keys)
        {
            var g = new Gradient();
            var ck = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                ColorUtility.TryParseHtmlString("#" + keys[i].hex, out var c);
                ck[i] = new GradientColorKey(c, keys[i].t);
            }
            g.SetKeys(ck, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) });
            return g;
        }

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
                var noise = Resources.Load<Texture2D>("Textures/cloud_noise");
                if (noise != null) SkyMaterial.SetTexture("_CloudNoise", noise);
                RenderSettings.skybox = SkyMaterial;
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = false;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            if (!Paused && DayLengthSeconds > 0) TimeOfDay = Mathf.Repeat(TimeOfDay + Time.deltaTime / DayLengthSeconds, 1f);
            Apply();
        }

        /// <summary>Direction towards the sun for a time of day (sun rises in +X, sets in -X, tilted south).</summary>
        public static Vector3 SunDirection(float t)
        {
            float a = (t - 0.25f) * Mathf.PI * 2f;
            var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            return Quaternion.AngleAxis(-28f, Vector3.right) * d;
        }

        public void Apply()
        {
            Vector3 sunDir = SunDirection(TimeOfDay).normalized;
            Vector3 moonDir = -sunDir;
            float sunH = sunDir.y;
            float day = Mathf.Clamp01((sunH + 0.08f) / 0.35f);        // 0 night .. 1 day
            float golden = Mathf.Clamp01(1f - Mathf.Abs(sunH - 0.08f) / 0.22f) * day; // warm low sun
            float overcast = Mathf.Clamp01(Overcast);

            // Main light: sun by day, moon by night.
            bool useSun = sunH > -0.05f;
            Vector3 lightDir = useSun ? sunDir : moonDir;
            if (lightDir.y < 0.06f) lightDir = new Vector3(lightDir.x, 0.06f, lightDir.z).normalized;
            Sun.transform.rotation = Quaternion.LookRotation(-lightDir);
            Color sunCol = Color.Lerp(new Color(1.0f, 0.62f, 0.38f), new Color(1.0f, 0.96f, 0.90f), Mathf.Clamp01(sunH * 3.2f));
            float sunI = Mathf.Lerp(0f, 3.1f, Mathf.Clamp01(sunH * 4f + 0.15f)) * (1f - overcast * 0.78f);
            Color moonCol = new Color(0.55f, 0.66f, 0.95f);
            float moonI = 0.32f * Mathf.Clamp01(moonDir.y * 4f) * (1f - overcast * 0.7f);
            Sun.color = useSun ? sunCol : moonCol;
            Sun.intensity = useSun ? sunI : moonI;
            Sun.shadowStrength = Mathf.Lerp(1f, 0.55f, overcast);

            // Sky colours.
            float band = Mathf.Clamp01(sunH * 0.5f + 0.5f);
            Color zen = sZenith.Evaluate(band).linear, hor = sHorizon.Evaluate(band).linear;
            Color grey = new Color(0.42f, 0.45f, 0.5f) * Mathf.Lerp(0.08f, 1f, day);
            zen = Color.Lerp(zen, grey.linear * 0.9f, overcast * 0.8f);
            hor = Color.Lerp(hor, grey.linear * 1.1f, overcast * 0.8f);

            // Ambient: bright hemisphere by day, cool and dim (but playable) at night.
            Color ambSky = Color.Lerp(new Color(0.045f, 0.06f, 0.11f), zen * 1.25f + hor * 0.25f, day);
            Color ambGround = Color.Lerp(new Color(0.02f, 0.025f, 0.035f), new Color(0.20f, 0.17f, 0.13f) * (0.4f + 0.6f * day), day);
            ambSky *= Mathf.Lerp(1f, 0.85f, overcast);
            Shader.SetGlobalVector(kSunDir, lightDir);
            Shader.SetGlobalVector(kRealSunDir, sunDir);
            Shader.SetGlobalVector(kMoonDir, new Vector4(moonDir.x, moonDir.y, moonDir.z, Mathf.Clamp01(moonDir.y * 5f + 0.2f) * (1f - overcast)));
            Shader.SetGlobalColor(kAmbSky, ambSky);
            Shader.SetGlobalColor(kAmbGround, ambGround);
            Shader.SetGlobalColor(kCave, new Color(0.012f, 0.012f, 0.016f));
            Shader.SetGlobalVector(kBlock, new Vector4(1.0f, 0.62f, 0.32f, 2.6f));
            Shader.SetGlobalColor(kZenith, zen);
            Shader.SetGlobalColor(kHorizon, hor);
            Shader.SetGlobalVector(kSunCol, new Vector4(sunCol.r * 1.4f, sunCol.g * 1.25f, sunCol.b * 1.1f, Mathf.Clamp01(sunH * 8f + 0.6f) * (1f - overcast * 0.9f)));
            Shader.SetGlobalFloat(kStars, Mathf.Clamp01(-sunH * 5f) * (1f - overcast));
            Shader.SetGlobalVector(kClouds, new Vector4(Mathf.Lerp(0.42f, 0.95f, overcast), Mathf.Lerp(0.25f, 0.85f, overcast), 1f, Mathf.Lerp(0.12f, 1.1f, day)));

            // Haze: matches the horizon, warmer towards the sun at golden hour.
            Color fog = Color.Lerp(hor, zen, 0.15f);
            Color fogSun = Color.Lerp(fog, sunCol * 0.9f + hor * 0.3f, 0.35f + golden * 0.45f);
            Shader.SetGlobalColor(kFogCol, fog);
            Shader.SetGlobalColor(kFogSun, fogSun);
            float density = 0.0028f + overcast * 0.004f + Rain * 0.006f + FogBoost * 0.01f;
            Shader.SetGlobalVector(kFog, new Vector4(density, 0.045f, 62f, 18f));
            Shader.SetGlobalVector(kFade, new Vector4(ViewDistanceMeters * 0.72f, ViewDistanceMeters * 0.97f, 0, 0));
            Shader.SetGlobalVector(kWind, new Vector4(0.8f, 0.6f, 0.7f + Rain * 0.8f + overcast * 0.3f, 0.5f + Rain * 0.5f));
            mWetness = Mathf.MoveTowards(mWetness, Rain > 0.2f ? 1f : 0f, Time.deltaTime / (Rain > 0.2f ? 25f : 90f));
            Shader.SetGlobalFloat(kWet, mWetness);
            Shader.SetGlobalFloat(kUnder, Underwater ? 1f : 0f);
            Shader.SetGlobalColor(kUnderCol, new Color(0.02f, 0.09f, 0.13f) * Mathf.Lerp(0.15f, 1f, day));

            RenderSettings.ambientSkyColor = ambSky;
            RenderSettings.ambientEquatorColor = Color.Lerp(ambSky, ambGround, 0.5f);
            RenderSettings.ambientGroundColor = ambGround;
        }
    }
}
