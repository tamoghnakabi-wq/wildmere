using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.Rendering
{
    public enum WeatherKind { Clear, Cloudy, Rain, Thunderstorm, Snow, Fog }

    /// <summary>
    /// Weather: a deterministic schedule (world seed + weather clock) of Clear / Cloudy / Rain /
    /// Thunderstorm / Snow / Fog spells lasting 5-15 minutes, with gradual transitions (clouds build over a
    /// couple of minutes before rain starts). Precipitation falls as snow in cold/snowy biomes and high up.
    /// Drives Atmosphere (cloud cover, darkness, fog, wind, wetness), the particle system, lightning (sky
    /// flash, bolt, <see cref="ThunderStrike"/> + delayed SoundHub thunder) and the audio statics.
    /// Persisted in the world save meta ("weather") through <see cref="GameRoot.Weather.WeatherState"/>.
    /// Harness: -weather clear|cloudy|rain|storm|snow|fog forces (and holds) a state instantly;
    /// -strikeat S triggers a lightning strike S seconds after the world is ready.
    /// </summary>
    public sealed class Weather : MonoBehaviour
    {
        public static Weather Instance { get; private set; }
        /// <summary>The weather the player is experiencing right now (derived from the live channels).</summary>
        public static WeatherKind Current { get; private set; } = WeatherKind.Clear;
        /// <summary>Rain falling at the camera's location, 0..1 (0 when it snows).</summary>
        public static float RainIntensity { get; private set; }
        /// <summary>Snow falling at the camera's location, 0..1.</summary>
        public static float SnowIntensity { get; private set; }
        /// <summary>Wind strength 0..1 (0.5 is a breeze, 1 a storm).</summary>
        public static float WindIntensity { get; private set; }
        /// <summary>Lightning strike: (world position of the strike, distance from the camera in metres). Raised at
        /// the flash; sound travels at ~343 m/s, so play thunder after distance / 343 s. Wildmere already plays
        /// "thunder/near"/"thunder/far" through SoundHub with that delay.</summary>
        public static event Action<Vector3, float> ThunderStrike;
        /// <summary>Raised when <see cref="Current"/> changes.</summary>
        public static event Action<WeatherKind> Changed;

        struct Target { public float Cover, Precip, Storm, Fog, Wind; }

        static Target TargetFor(WeatherKind k)
        {
            switch (k)
            {
                case WeatherKind.Cloudy: return new Target { Cover = 0.74f, Precip = 0f, Storm = 0f, Fog = 0.05f, Wind = 0.85f };
                case WeatherKind.Rain: return new Target { Cover = 0.92f, Precip = 0.72f, Storm = 0.2f, Fog = 0.15f, Wind = 1.1f };
                case WeatherKind.Thunderstorm: return new Target { Cover = 1f, Precip = 1f, Storm = 1f, Fog = 0.2f, Wind = 1.8f };
                case WeatherKind.Snow: return new Target { Cover = 0.9f, Precip = 0.75f, Storm = 0f, Fog = 0.25f, Wind = 0.7f };
                case WeatherKind.Fog: return new Target { Cover = 0.55f, Precip = 0f, Storm = 0f, Fog = 1f, Wind = 0.12f };
                default: return new Target { Cover = 0.32f, Precip = 0f, Storm = 0f, Fog = 0f, Wind = 0.5f };
            }
        }

        // Live channels (eased towards the scheduled target).
        float mCover = 0.32f, mPrecip, mStorm, mFog, mWind = 0.5f, mSnowFrac, mSnowTarget;
        WeatherKind mScheduled = WeatherKind.Clear;
        double mClock, mSegEnd;
        int mSegIndex;
        long mSeed;
        bool mForced, mActive;
        float mClimateTimer, mStrikeTimer = 8f, mFlashT = 99f;
        Vector2 mWindDir = new Vector2(0.8f, 0.6f);
        float mWindAngle;
        LineRenderer mBolt;
        float mBoltTimer;
        WeatherParticles mParticles;

        static readonly WeatherKind[] kPool =
            { WeatherKind.Clear, WeatherKind.Clear, WeatherKind.Clear, WeatherKind.Clear, WeatherKind.Cloudy, WeatherKind.Cloudy,
              WeatherKind.Cloudy, WeatherKind.Rain, WeatherKind.Rain, WeatherKind.Rain, WeatherKind.Thunderstorm, WeatherKind.Fog, WeatherKind.Fog };

        void Awake()
        {
            Instance = this;
            GameRoot.Weather.WeatherState = WriteState;
            mParticles = gameObject.AddComponent<WeatherParticles>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (GameRoot.Weather.WeatherState == (Action<Dictionary<string, object>>)WriteState) GameRoot.Weather.WeatherState = null;
        }

        void Start()
        {
            if (GameRoot.Instance != null)
            {
                GameRoot.Instance.WorldReady += OnWorldReady;
                GameRoot.Instance.WorldClosing += () => mActive = false;
            }
        }

        void OnWorldReady()
        {
            var root = GameRoot.Instance;
            mSeed = root.Save != null ? root.Save.Seed : 1337;
            mActive = true;
            mForced = false;
            var w = root.Save != null ? Json.Obj(root.Save.Meta, "weather") : null;
            if (w != null)
            {
                mClock = Json.Num(w, "clock");
                mSegEnd = Json.Num(w, "segEnd");
                mSegIndex = (int)Json.Num(w, "seg");
                Enum.TryParse(Json.Str(w, "kind", "Clear"), out mScheduled);
                mCover = (float)Json.Num(w, "cover", 0.32);
                mPrecip = (float)Json.Num(w, "precip");
                mStorm = (float)Json.Num(w, "storm");
                mFog = (float)Json.Num(w, "fog");
                mWind = (float)Json.Num(w, "wind", 0.5);
                mWindAngle = (float)Json.Num(w, "windAngle", 37);
                Debug.Log($"WM_WEATHER restored kind={mScheduled} clock={mClock:F0} segEnd={mSegEnd:F0} cover={mCover:F2} precip={mPrecip:F2}");
            }
            else
            {
                // A fresh world starts with a clear first spell.
                mClock = 0; mSegIndex = 0; mScheduled = WeatherKind.Clear;
                mSegEnd = 240 + Hash01(mSeed, 0, 1) * 360;
                var t = TargetFor(mScheduled);
                mCover = t.Cover; mPrecip = 0; mStorm = 0; mFog = 0; mWind = t.Wind;
                mWindAngle = Hash01(mSeed, 0, 9) * 360f;
            }
            ClimateSample(true);
            mSnowFrac = mSnowTarget;

            var forced = Cli.Str("weather");
            if (forced != null) Force(Parse(forced), true);
            float strikeAt = Cli.Float("strikeat", -1);
            if (strikeAt >= 0) StartCoroutine(StrikeAfter(strikeAt));
        }

        IEnumerator StrikeAfter(float s)
        {
            // Offscreen captures time their shots from when the view finished streaming; strike on that clock.
            if (OffscreenCapture.Wanted && Cli.Has("gfxshots"))
                while (OffscreenCapture.TimerStart < 0) yield return null;
            yield return new WaitForSecondsRealtime(s);
            Strike(true);
        }

        public static WeatherKind Parse(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "storm": case "thunder": case "thunderstorm": return WeatherKind.Thunderstorm;
                case "rain": return WeatherKind.Rain;
                case "snow": return WeatherKind.Snow;
                case "fog": return WeatherKind.Fog;
                case "cloudy": case "clouds": return WeatherKind.Cloudy;
                default: return WeatherKind.Clear;
            }
        }

        /// <summary>Hold a weather state (debug/harness). Instant snaps every channel to the target.</summary>
        public void Force(WeatherKind k, bool instant)
        {
            mForced = true;
            mScheduled = k;
            if (k == WeatherKind.Snow) mSnowTarget = mSnowFrac = 1f;
            else if (k == WeatherKind.Rain || k == WeatherKind.Thunderstorm) mSnowTarget = mSnowFrac = 0f;
            if (instant)
            {
                var t = TargetFor(k);
                mCover = t.Cover; mPrecip = t.Precip; mStorm = t.Storm; mFog = t.Fog; mWind = t.Wind;
                var atm = Atmosphere.Instance;
                if (atm != null) atm.SnowCover = k == WeatherKind.Snow ? 1f : 0f;
            }
            Debug.Log($"WM_WEATHER forced={k} instant={instant}");
        }

        static float Hash01(long seed, int a, int b)
        {
            unchecked
            {
                ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(a * 0x632BE5AB) ^ ((ulong)b << 32);
                h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
                return (h >> 11) * (1.0f / 9007199254740992f);
            }
        }

        void NextSegment()
        {
            mSegIndex++;
            float r = Hash01(mSeed, mSegIndex, 2);
            var prev = mScheduled;
            WeatherKind next = kPool[Mathf.Min(kPool.Length - 1, (int)(r * kPool.Length))];
            // Storms break up into rain or clouds rather than flipping straight to sunshine; fog likes mornings.
            if (prev == WeatherKind.Thunderstorm && next == WeatherKind.Clear) next = WeatherKind.Rain;
            float tod = Atmosphere.Instance != null ? Atmosphere.Instance.TimeOfDay : 0.5f;
            if (next == WeatherKind.Fog && (tod > 0.45f && tod < 0.85f) && Hash01(mSeed, mSegIndex, 3) < 0.6f) next = WeatherKind.Cloudy;
            if (next == prev && Hash01(mSeed, mSegIndex, 4) < 0.5f) next = WeatherKind.Clear;
            mScheduled = next;
            mSegEnd = mClock + 300 + Hash01(mSeed, mSegIndex, 5) * 600;
            Debug.Log($"WM_WEATHER next={next} seg={mSegIndex} until={mSegEnd:F0}");
        }

        void ClimateSample(bool now)
        {
            var w = VoxelWorld.Instance;
            var cam = Camera.main;
            if (w == null || w.Gen == null || cam == null) return;
            var p = cam.transform.position;
            var s = w.Gen.SampleBiome(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
            bool cold = s.Snowy || s.Biome == Biome.SnowyMountains || s.Biome == Biome.SnowyPlains || s.Temperature < 0.18f || p.y > 150f;
            if (mScheduled == WeatherKind.Snow && mForced) cold = true;
            mSnowTarget = cold ? 1f : 0f;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            var atm = Atmosphere.Instance;
            bool playing = mActive && GameRoot.Instance != null && GameRoot.Instance.State != GameState.MainMenu;
            if (!playing)
            {
                // Menu backdrop: calm, fair weather.
                mCover = Mathf.MoveTowards(mCover, 0.35f, dt * 0.2f); mPrecip = 0; mStorm = 0; mFog = 0; mWind = 0.5f;
            }
            else
            {
                mClock += dt;
                if (!mForced && mClock >= mSegEnd) NextSegment();
                mClimateTimer -= dt;
                if (mClimateTimer <= 0) { mClimateTimer = 1f; ClimateSample(false); }

                var t = TargetFor(mScheduled);
                // Clouds build first; precipitation only starts under a heavy sky and stops before it clears.
                mCover = Mathf.MoveTowards(mCover, t.Cover, dt / 150f);
                float precipTarget = mCover > t.Cover - 0.12f ? t.Precip : 0f;
                mPrecip = Mathf.MoveTowards(mPrecip, precipTarget, dt / 50f);
                mStorm = Mathf.MoveTowards(mStorm, mPrecip > 0.5f ? t.Storm : 0f, dt / 40f);
                mFog = Mathf.MoveTowards(mFog, t.Fog, dt / 90f);
                mWind = Mathf.MoveTowards(mWind, t.Wind, dt / 25f);
            }
            mSnowFrac = Mathf.MoveTowards(mSnowFrac, mSnowTarget, dt / 6f);
            mWindAngle += dt * (0.4f + mWind * 0.8f) * Mathf.Sin((float)mClock * 0.004f + 1.3f);
            mWindDir = new Vector2(Mathf.Cos(mWindAngle * Mathf.Deg2Rad), Mathf.Sin(mWindAngle * Mathf.Deg2Rad));

            // Report.
            RainIntensity = mPrecip * (1f - mSnowFrac);
            SnowIntensity = mPrecip * mSnowFrac;
            WindIntensity = Mathf.Clamp01(mWind / 2f);
            var cur = mStorm > 0.5f && RainIntensity > 0.3f ? WeatherKind.Thunderstorm
                : SnowIntensity > 0.2f ? WeatherKind.Snow
                : RainIntensity > 0.2f ? WeatherKind.Rain
                : mFog > 0.5f ? WeatherKind.Fog
                : mCover > 0.62f ? WeatherKind.Cloudy : WeatherKind.Clear;
            if (cur != Current) { Current = cur; Debug.Log($"WM_WEATHER now={cur}"); Changed?.Invoke(cur); }
            Audio.AudioManager.Rain = RainIntensity;
            Audio.AudioManager.Snow = SnowIntensity;
            Audio.AudioManager.Wind = WindIntensity;

            // Lightning.
            if (playing && mStorm > 0.6f && mPrecip > 0.5f)
            {
                mStrikeTimer -= dt;
                if (mStrikeTimer <= 0) { mStrikeTimer = UnityEngine.Random.Range(5f, 18f) / Mathf.Max(0.5f, mStorm); Strike(false); }
            }
            mFlashT += dt;
            float flash = FlashEnvelope(mFlashT);
            if (mBolt != null)
            {
                mBoltTimer -= dt;
                mBolt.enabled = mBoltTimer > 0;
                if (mBolt.enabled && mBolt.sharedMaterial != null) mBolt.sharedMaterial.SetVector("_WxParams2", new Vector4(0, 0.35f + flash * 1.4f, 0, 0));
            }

            if (atm != null)
            {
                atm.Overcast = mCover;
                atm.Rain = mPrecip;
                atm.Snow = mSnowFrac;
                atm.Storm = mStorm;
                atm.FogBoost = mFog;
                atm.WindStrength = mWind;
                atm.WindDir = mWindDir;
                atm.Flash = flash;
            }
            if (mParticles != null) mParticles.SetState(RainIntensity, SnowIntensity, mWind, mWindDir, flash);
        }

        static float FlashEnvelope(float t)
        {
            if (t > 1.2f) return 0f;
            float a = Mathf.Exp(-t * 14f);
            float b = t > 0.13f ? Mathf.Exp(-(t - 0.13f) * 9f) * 0.8f : 0f;
            float c = t > 0.34f ? Mathf.Exp(-(t - 0.34f) * 12f) * 0.45f : 0f;
            return Mathf.Clamp01(a + b + c);
        }

        void Strike(bool inView)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            Vector3 dir;
            if (inView)
            {
                var f = cam.transform.forward; f.y = 0;
                dir = Quaternion.Euler(0, UnityEngine.Random.Range(-18f, 18f), 0) * f.normalized;
            }
            else
            {
                float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            }
            float dist = inView ? UnityEngine.Random.Range(140f, 260f) : UnityEngine.Random.Range(90f, 650f);
            Vector3 pos = c + dir * dist;
            int gy = VoxelWorld.Instance != null ? VoxelWorld.Instance.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z)) : -1;
            pos.y = gy > 0 ? gy + 1 : 64f;
            mFlashT = 0f;
            if (Atmosphere.Instance != null) Atmosphere.Instance.FlashDir = (pos + Vector3.up * 120f - c).normalized;
            Bolt(pos);
            ThunderStrike?.Invoke(pos, dist);
            StartCoroutine(Thunder(pos, dist));
            Debug.Log($"WM_WEATHER strike at={pos} dist={dist:F0}m");
        }

        IEnumerator Thunder(Vector3 pos, float dist)
        {
            yield return new WaitForSeconds(dist / 343f);
            SoundHub.At(dist < 260f ? "thunder/near" : "thunder/far", pos, Mathf.Clamp01(1.2f - dist / 900f));
        }

        void Bolt(Vector3 ground)
        {
            if (mBolt == null)
            {
                var go = new GameObject("LightningBolt");
                go.transform.SetParent(transform, false);
                mBolt = go.AddComponent<LineRenderer>();
                var sh = Shader.Find("Wildmere/WeatherFx");
                mBolt.sharedMaterial = sh != null ? new Material(sh) { name = "Bolt", enableInstancing = false } : null;
                if (mBolt.sharedMaterial != null)
                {
                    mBolt.sharedMaterial.SetFloat("_Mode", 2f);
                    mBolt.sharedMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                    mBolt.sharedMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                }
                mBolt.startColor = Color.white; mBolt.endColor = Color.white;
                mBolt.textureMode = LineTextureMode.Stretch;
                mBolt.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mBolt.receiveShadows = false;
                mBolt.useWorldSpace = true;
                mBolt.numCapVertices = 2;
                mBolt.alignment = LineAlignment.View;
            }
            var pts = new List<Vector3>();
            float top = 200f;
            var p = new Vector3(ground.x + UnityEngine.Random.Range(-30f, 30f), top, ground.z + UnityEngine.Random.Range(-30f, 30f));
            int n = 26;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                var target = Vector3.Lerp(p, ground, t);
                var jit = new Vector3(UnityEngine.Random.Range(-7f, 7f), 0, UnityEngine.Random.Range(-7f, 7f)) * (1f - t * 0.7f);
                pts.Add(i == n ? ground : target + jit);
            }
            mBolt.positionCount = pts.Count;
            mBolt.SetPositions(pts.ToArray());
            float wdt = Mathf.Lerp(0.9f, 2.4f, Mathf.InverseLerp(100f, 600f, Vector3.Distance(Camera.main.transform.position, ground)));
            mBolt.widthCurve = new AnimationCurve(new Keyframe(0, wdt * 0.6f), new Keyframe(0.7f, wdt), new Keyframe(1f, wdt * 1.2f));
            mBolt.enabled = true;
            mBoltTimer = 0.42f;
        }

        void WriteState(Dictionary<string, object> meta)
        {
            meta["weather"] = new Dictionary<string, object>
            {
                { "kind", mScheduled.ToString() }, { "clock", mClock }, { "segEnd", mSegEnd }, { "seg", (double)mSegIndex },
                { "cover", (double)mCover }, { "precip", (double)mPrecip }, { "storm", (double)mStorm }, { "fog", (double)mFog },
                { "wind", (double)mWind }, { "windAngle", (double)mWindAngle },
            };
        }
    }
}
