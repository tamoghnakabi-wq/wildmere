using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Wildmere.Core
{
    public enum GraphicsPreset { Low = 0, Medium = 1, High = 2, Ultra = 3, Custom = 4 }

    /// <summary>Rebindable actions (legacy input KeyCodes, mouse buttons included).</summary>
    public enum Control { Forward, Back, Left, Right, Jump, Sprint, Crouch, Inventory, Drop, Pause, Attack, Use, PickBlock, Screenshot, ToggleHud }

    /// <summary>
    /// Player-wide settings (not per world), persisted as settings.json in persistentDataPath.
    /// Graphics fields are the "custom" values; picking a preset overwrites them.
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        // Graphics
        public GraphicsPreset Preset = GraphicsPreset.High;
        public int ViewDistance = 10;          // chunks
        public float RenderScale = 1f;
        public int ShadowQuality = 2;          // 0 off, 1 low, 2 medium, 3 high
        public float ShadowDistance = 96f;
        public bool Ssao = true;
        public int Antialiasing = 2;           // 0 off, 1 FXAA, 2 MSAA 2x, 3 MSAA 4x
        public bool Bloom = true;
        public bool FancyLeaves = true;
        public bool Clouds = true;
        public int WeatherParticles = 2;       // 0 low .. 2 high
        public bool WaterReflections = true;
        public bool VSync = true;
        public int FpsCap = 0;                 // 0 = uncapped (vsync governs)
        public float Fov = 75f;
        public bool HeadBob = true;
        public bool CameraShake = true;
        public float Brightness = 0f;          // -1..1 exposure offset
        public int Fullscreen = 0;             // 0 windowed, 1 borderless, 2 exclusive

        // Audio (0..1)
        public float MasterVolume = 0.9f, MusicVolume = 0.55f, AmbienceVolume = 0.8f, EffectsVolume = 0.85f, UiVolume = 0.7f;

        // Controls
        public float MouseSensitivity = 1f;
        public bool InvertY;
        public bool AutoJump;
        public List<string> Bindings = new List<string>();   // "Action=KeyCode"

        [NonSerialized] Dictionary<Control, KeyCode> mKeys;

        public static GameSettings Current { get; private set; } = new GameSettings();
        public static event System.Action Changed;

        static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");

        public static readonly Dictionary<Control, KeyCode> DefaultKeys = new Dictionary<Control, KeyCode>
        {
            { Control.Forward, KeyCode.W }, { Control.Back, KeyCode.S }, { Control.Left, KeyCode.A }, { Control.Right, KeyCode.D },
            { Control.Jump, KeyCode.Space }, { Control.Sprint, KeyCode.LeftControl }, { Control.Crouch, KeyCode.LeftShift },
            { Control.Inventory, KeyCode.E }, { Control.Drop, KeyCode.Q }, { Control.Pause, KeyCode.Escape },
            { Control.Attack, KeyCode.Mouse0 }, { Control.Use, KeyCode.Mouse1 }, { Control.PickBlock, KeyCode.Mouse2 },
            { Control.Screenshot, KeyCode.F2 }, { Control.ToggleHud, KeyCode.F1 },
        };

        public KeyCode Key(Control a)
        {
            if (mKeys == null)
            {
                mKeys = new Dictionary<Control, KeyCode>(DefaultKeys);
                foreach (var b in Bindings)
                {
                    var p = b.Split('=');
                    if (p.Length == 2 && Enum.TryParse(p[0], out Control act) && Enum.TryParse(p[1], out KeyCode kc)) mKeys[act] = kc;
                }
            }
            return mKeys.TryGetValue(a, out var k) ? k : KeyCode.None;
        }

        public void Bind(Control a, KeyCode k)
        {
            Key(a);
            mKeys[a] = k;
            Bindings.Clear();
            foreach (var kv in mKeys)
                if (!DefaultKeys.TryGetValue(kv.Key, out var d) || d != kv.Value) Bindings.Add($"{kv.Key}={kv.Value}");
        }

        public void ResetBindings() { Bindings.Clear(); mKeys = null; }

        public static bool Held(Control a) => Input.GetKey(Current.Key(a));
        public static bool Pressed(Control a) => Input.GetKeyDown(Current.Key(a));
        public static bool Released(Control a) => Input.GetKeyUp(Current.Key(a));

        /// <summary>Overwrite the graphics fields with a preset's values.</summary>
        public void ApplyPreset(GraphicsPreset p)
        {
            Preset = p;
            switch (p)
            {
                case GraphicsPreset.Low:
                    ViewDistance = 6; RenderScale = 0.8f; ShadowQuality = 1; ShadowDistance = 48; Ssao = false; Antialiasing = 1;
                    Bloom = false; FancyLeaves = false; Clouds = true; WeatherParticles = 0; WaterReflections = false; break;
                case GraphicsPreset.Medium:
                    ViewDistance = 8; RenderScale = 0.9f; ShadowQuality = 2; ShadowDistance = 72; Ssao = false; Antialiasing = 1;
                    Bloom = true; FancyLeaves = true; Clouds = true; WeatherParticles = 1; WaterReflections = true; break;
                case GraphicsPreset.High:
                    ViewDistance = 10; RenderScale = 1f; ShadowQuality = 2; ShadowDistance = 96; Ssao = true; Antialiasing = 2;
                    Bloom = true; FancyLeaves = true; Clouds = true; WeatherParticles = 2; WaterReflections = true; break;
                case GraphicsPreset.Ultra:
                    ViewDistance = 14; RenderScale = 1f; ShadowQuality = 3; ShadowDistance = 140; Ssao = true; Antialiasing = 3;
                    Bloom = true; FancyLeaves = true; Clouds = true; WeatherParticles = 2; WaterReflections = true; break;
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath)) Current = JsonUtility.FromJson<GameSettings>(File.ReadAllText(FilePath)) ?? new GameSettings();
            }
            catch (Exception e) { Debug.LogWarning($"WM_SETTINGS unreadable, using defaults: {e.Message}"); Current = new GameSettings(); }
        }

        public static void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(Current, true)); }
            catch (Exception e) { Debug.LogWarning($"WM_SETTINGS save failed: {e.Message}"); }
            Changed?.Invoke();
        }

        /// <summary>Notify listeners (graphics, audio) without writing to disk.</summary>
        public static void NotifyChanged() => Changed?.Invoke();
    }
}
