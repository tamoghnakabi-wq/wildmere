using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.Player;
using Wildmere.World;

namespace Wildmere.Audio
{
    /// <summary>
    /// Runtime audio: loads the bank described by Resources/Audio/audio.json, plays positional one-shots
    /// from gameplay events (footsteps, mining, placing, pickups, hurt, splashes, wildlife via SoundHub),
    /// crossfades ambience beds by biome / time of day / weather / cave / underwater, and plays occasional
    /// music. Missing sounds are skipped silently, so the game runs with any subset of the bank.
    /// </summary>
    public sealed class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        sealed class Entry { public AudioClip[] Clips; public float Volume = 1f; public bool Loop; public int Last = -1; }
        enum Cat { Effects, Ambience, Music, Ui }

        readonly Dictionary<string, Entry> mBank = new Dictionary<string, Entry>();
        readonly List<AudioSource> mPool3D = new List<AudioSource>(), mPool2D = new List<AudioSource>();
        int mNext3D, mNext2D;

        sealed class Bed { public AudioSource A, B; public string Key; public float Target; public float Cur; }
        readonly Dictionary<string, Bed> mBeds = new Dictionary<string, Bed>();
        AudioSource mMusic;
        float mMusicTimer = 40f, mBiomeTimer;
        string mBiomeBed;
        bool mCave;
        PlayerController mBoundPlayer;
        BlockInteraction mBoundInteraction;
        Transform mListener;

        /// <summary>Weather systems set these (0..1); ambience follows.</summary>
        public static float Rain, Wind, Snow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject("Audio");
            DontDestroyOnLoad(go);
            go.AddComponent<AudioManager>();
        }

        void Awake()
        {
            Instance = this;
            LoadBank();
            for (int i = 0; i < 28; i++) mPool3D.Add(NewSource(true));
            for (int i = 0; i < 8; i++) mPool2D.Add(NewSource(false));
            mMusic = NewSource(false);
            SoundHub.Positional += (k, p, v) => PlayAt(k, p, v);
            SoundHub.Flat += (k, v) => Play2D(k, v, k.StartsWith("ui/") ? Cat.Ui : Cat.Effects);
            ItemDrop.PickedUp += d => PlayAt("item/pickup", d.transform.position, 0.6f, 1f + Random.Range(-0.1f, 0.15f));
            AudioListener.volume = GameSettings.Current.MasterVolume;
            GameSettings.Changed += () => AudioListener.volume = GameSettings.Current.MasterVolume;
            Debug.Log($"WM_AUDIO bank={mBank.Count} entries");
        }

        AudioSource NewSource(bool spatial)
        {
            var go = new GameObject(spatial ? "sfx3d" : "sfx2d");
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 1.5f;
            s.maxDistance = 48f;
            s.dopplerLevel = 0f;
            return s;
        }

        void LoadBank()
        {
            var ta = Resources.Load<TextAsset>("Audio/audio");
            if (ta == null) { Debug.Log("WM_AUDIO no audio.json yet"); return; }
            if (!(Json.Parse(ta.text) is Dictionary<string, object> root)) return;
            foreach (var kv in root)
            {
                var files = Json.Arr(kv.Value, "files");
                if (files == null) continue;
                var clips = new List<AudioClip>();
                foreach (var f in files)
                {
                    string path = (f as string ?? "").Replace("\\", "/");
                    if (path.StartsWith("Assets/Resources/")) path = path.Substring("Assets/Resources/".Length);
                    int dot = path.LastIndexOf('.');
                    if (dot > path.LastIndexOf('/')) path = path.Substring(0, dot);
                    var clip = Resources.Load<AudioClip>(path) ?? Resources.Load<AudioClip>("Audio/" + path);
                    if (clip != null) clips.Add(clip);
                }
                if (clips.Count == 0) continue;
                mBank[kv.Key] = new Entry
                {
                    Clips = clips.ToArray(),
                    Volume = (float)Json.Num(kv.Value, "volume", 1),
                    Loop = Json.Bool(kv.Value, "loop"),
                };
            }
        }

        public bool Has(string key) => mBank.ContainsKey(key);

        AudioClip Pick(Entry e)
        {
            if (e.Clips.Length == 1) return e.Clips[0];
            int i = Random.Range(0, e.Clips.Length);
            if (i == e.Last) i = (i + 1) % e.Clips.Length;
            e.Last = i;
            return e.Clips[i];
        }

        static float CatVolume(Cat c)
        {
            var s = GameSettings.Current;
            switch (c)
            {
                case Cat.Ambience: return s.AmbienceVolume;
                case Cat.Music: return s.MusicVolume;
                case Cat.Ui: return s.UiVolume;
                default: return s.EffectsVolume;
            }
        }

        public void PlayAt(string key, Vector3 pos, float volume = 1f, float pitch = -1f)
        {
            if (!mBank.TryGetValue(key, out var e)) return;
            var s = mPool3D[mNext3D];
            mNext3D = (mNext3D + 1) % mPool3D.Count;
            s.transform.position = pos;
            s.clip = Pick(e);
            s.volume = e.Volume * volume * CatVolume(Cat.Effects) * (mUnderwater ? 0.45f : 1f);
            s.pitch = pitch > 0 ? pitch : Random.Range(0.94f, 1.06f) * (mUnderwater ? 0.8f : 1f);
            s.loop = false;
            s.Play();
        }

        void Play2D(string key, float volume, Cat cat)
        {
            if (!mBank.TryGetValue(key, out var e)) return;
            var s = mPool2D[mNext2D];
            mNext2D = (mNext2D + 1) % mPool2D.Count;
            s.clip = Pick(e);
            s.volume = e.Volume * volume * CatVolume(cat);
            s.pitch = cat == Cat.Ui ? 1f : Random.Range(0.96f, 1.04f);
            s.Play();
        }

        // ------------------------------------------------------------------ gameplay bindings

        static string Mat(SoundMat m, bool step)
        {
            switch (m)
            {
                case SoundMat.Stone: return "stone";
                case SoundMat.Dirt: return "dirt";
                case SoundMat.Grass: return step ? "grass" : "dirt";
                case SoundMat.Sand: return "sand";
                case SoundMat.Gravel: return "gravel";
                case SoundMat.Wood: return "wood";
                case SoundMat.Leaves: return "leaves";
                case SoundMat.Glass: return "glass";
                case SoundMat.Snow: return "snow";
                case SoundMat.Water: return "water";
                default: return step ? "grass" : "leaves";
            }
        }

        void Bind(PlayerController p, BlockInteraction bi)
        {
            if (p == mBoundPlayer && bi == mBoundInteraction) return;
            mBoundPlayer = p; mBoundInteraction = bi;
            if (p != null)
            {
                p.Footstep += m => PlayAt("step/" + Mat(m, true), p.transform.position, p.Crouching ? 0.45f : p.Sprinting ? 1f : 0.8f);
                p.Landed += fall => { if (fall > 2.5f) PlayAt("player/land_hard", p.transform.position, Mathf.Clamp01(fall / 8f) + 0.3f); };
                p.Damaged += (amt, cause) => Play2D("player/hurt", 0.8f, Cat.Effects);
                p.WaterTransition += inWater => PlayAt(inWater ? "water/splash_in" : "water/splash_out", p.transform.position, inWater ? 0.9f : 0.6f);
            }
            if (bi != null)
            {
                bi.MineHit += (d, b) => PlayAt("dig/" + Mat(d.Sound, false), b + Vector3.one * 0.5f, 0.55f);
                bi.BlockBroken += (d, b) => PlayAt("break/" + Mat(d.Sound, false), b + Vector3.one * 0.5f, 0.9f);
                bi.BlockPlaced += (d, b) => PlayAt(d.Id == B.Torch ? "torch/place" : "place/" + Mat(d.Sound, false), b + Vector3.one * 0.5f, 0.8f);
                bi.ToolBroke += _ => Play2D("item/tool_break", 0.9f, Cat.Effects);
            }
        }

        // ------------------------------------------------------------------ ambience

        Bed BedFor(string slot)
        {
            if (mBeds.TryGetValue(slot, out var b)) return b;
            b = new Bed { A = NewSource(false), B = NewSource(false) };
            b.A.loop = b.B.loop = true;
            mBeds[slot] = b;
            return b;
        }

        /// <summary>Crossfade a bed slot to a looping clip at a target loudness (0 = silent).</summary>
        void SetBed(string slot, string key, float target)
        {
            var b = BedFor(slot);
            if (key != null && !mBank.ContainsKey(key)) { key = null; target = 0; }
            if (key != b.Key)
            {
                // Swap: the old source fades out while the new one fades in.
                (b.A, b.B) = (b.B, b.A);
                b.Key = key;
                if (key != null)
                {
                    var e = mBank[key];
                    b.A.clip = Pick(e);
                    b.A.volume = 0;
                    b.A.time = Random.Range(0f, Mathf.Max(0f, b.A.clip.length - 1f));
                    b.A.Play();
                }
                b.Cur = 0;
            }
            b.Target = key == null ? 0 : target * mBank[key].Volume;
        }

        bool mUnderwater;

        void Update()
        {
            var root = GameRoot.Instance;
            float dt = Time.unscaledDeltaTime;
            var cam = Camera.main;
            if (cam != null && mListener != cam.transform) mListener = cam.transform;

            var p = root != null ? root.Player : null;
            if (p != null) Bind(p, root.Interaction);

            var w = VoxelWorld.Instance;
            var atm = Rendering.Atmosphere.Instance;
            float t = atm != null ? atm.TimeOfDay : 0.4f;
            float day = Mathf.Clamp01((Mathf.Sin((t - 0.25f) * Mathf.PI * 2f) + 0.15f) * 3f);

            mUnderwater = p != null && p.HeadUnderwater;
            if (root != null && (root.State == GameState.Playing || root.State == GameState.Paused) && p != null && w != null)
            {
                mBiomeTimer -= dt;
                if (mBiomeTimer <= 0)
                {
                    mBiomeTimer = 1f;
                    var pos = p.transform.position;
                    int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z);
                    var b = w.Gen.SampleBiome(x, z);
                    var c = w.ChunkAt(x, z);
                    mCave = c != null && c.SkyTop[(x & 15) + ((z & 15) << 4)] > pos.y + 4 && pos.y < 58;
                    mBiomeBed = BiomeBed(b.Biome, day);
                }
                float pause = root.State == GameState.Paused ? 0.35f : 1f;
                float amb = GameSettings.Current.AmbienceVolume * pause;
                SetBed("biome", mUnderwater || mCave ? null : mBiomeBed, amb * (1f - Rain * 0.6f));
                SetBed("night", mUnderwater || mCave || day > 0.5f ? null : "amb/night", amb * (1f - day * 2f) * 0.8f);
                SetBed("rain", Rain > 0.05f && !mCave && !mUnderwater ? (Rain > 0.6f ? "amb/rain_heavy" : "amb/rain_light") : null, amb * Mathf.Clamp01(Rain * 1.4f));
                SetBed("wind", (Wind > 0.4f || Snow > 0.2f) && !mCave && !mUnderwater ? "amb/wind_strong" : null, amb * Mathf.Clamp01(Mathf.Max(Wind, Snow) - 0.2f));
                SetBed("special", mUnderwater ? "amb/underwater" : mCave ? "amb/cave" : null, amb);
            }
            else if (root != null && root.State == GameState.MainMenu)
            {
                float amb = GameSettings.Current.AmbienceVolume * 0.6f;
                SetBed("biome", "amb/plains_day", amb);
                SetBed("night", null, 0); SetBed("rain", null, 0); SetBed("wind", null, 0); SetBed("special", null, 0);
            }
            else foreach (var k in new[] { "biome", "night", "rain", "wind", "special" }) SetBed(k, null, 0);

            foreach (var bed in mBeds.Values)
            {
                bed.Cur = Mathf.MoveTowards(bed.Cur, bed.Target, dt * 0.35f);
                bed.A.volume = bed.Cur;
                bed.B.volume = Mathf.MoveTowards(bed.B.volume, 0, dt * 0.35f);
                if (bed.B.volume <= 0.001f && bed.B.isPlaying) bed.B.Stop();
                if (bed.A.volume <= 0.001f && bed.Key == null && bed.A.isPlaying) bed.A.Stop();
            }

            // Music: a track every few minutes, never under water or in menus' loading.
            mMusicTimer -= dt;
            if (!mMusic.isPlaying && mMusicTimer <= 0 && !mUnderwater)
            {
                var music = new List<string>();
                foreach (var k in mBank.Keys) if (k.StartsWith("music/")) music.Add(k);
                if (music.Count > 0)
                {
                    var e = mBank[music[Random.Range(0, music.Count)]];
                    mMusic.clip = Pick(e);
                    mMusic.volume = e.Volume * GameSettings.Current.MusicVolume * 0.7f;
                    mMusic.Play();
                }
                mMusicTimer = Random.Range(180f, 420f);
            }
            if (mMusic.isPlaying) mMusic.volume = Mathf.MoveTowards(mMusic.volume, mUnderwater ? 0f : GameSettings.Current.MusicVolume * 0.7f, dt * 0.2f);
        }

        static string BiomeBed(Biome b, float day)
        {
            switch (b)
            {
                case Biome.TemperateForest: return day > 0.4f ? "amb/forest_day" : null;
                case Biome.DarkForest: return "amb/dark_forest";
                case Biome.Desert: return "amb/desert_wind";
                case Biome.SnowyMountains: case Biome.SnowyPlains: case Biome.Mountains: return "amb/snow_wind";
                case Biome.Ocean: case Biome.Beach: return "amb/ocean_shore";
                case Biome.River: return "amb/river";
                default: return day > 0.4f ? "amb/plains_day" : null;
            }
        }
    }
}
