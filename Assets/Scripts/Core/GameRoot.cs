using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Wildmere.Items;
using Wildmere.Player;
using Wildmere.Rendering;
using Wildmere.World;
using Wildmere.World.Decor;
using Wildmere.World.Gen;

namespace Wildmere.Core
{
    public enum GameState { Boot, MainMenu, Loading, Playing, Paused }

    /// <summary>
    /// Composition root and game-state machine. Everything is built at runtime from code: the scene
    /// only needs this component (and it is created automatically if missing).
    ///
    /// UI contract (for the UI module): read <see cref="State"/>/<see cref="StateChanged"/>, call
    /// <see cref="NewWorld"/>, <see cref="LoadWorld"/>, <see cref="Pause"/>, <see cref="Resume"/>,
    /// <see cref="SaveWorld"/>, <see cref="QuitToMenu"/>, and set <see cref="UiCapture"/> while a
    /// screen (inventory, crafting) needs the mouse during play.
    /// </summary>
    public sealed class GameRoot : MonoBehaviour
    {
        public static GameRoot Instance { get; private set; }
        public static string Version { get; private set; } = "0.1.0";

        public GameState State { get; private set; } = GameState.Boot;
        public event Action<GameState> StateChanged;
        /// <summary>Raised right after a world finished loading and the player can move.</summary>
        public event Action WorldReady;
        /// <summary>Raised before the world is torn down (save first if you need to).</summary>
        public event Action WorldClosing;
        public event Action<Station> OpenCrafting;
        /// <summary>Raised after the world was written to disk.</summary>
        public event Action Saved;

        public WorldSave Save { get; private set; }
        public VoxelWorld World { get; private set; }
        public PlayerController Player { get; private set; }
        public BlockInteraction Interaction { get; private set; }
        public Atmosphere Atmos { get; private set; }
        public bool NoSave { get; private set; }
        public float LoadProgress { get; private set; }
        public float PlayTime { get; private set; }

        bool mUiCapture;
        float mAutosaveTimer;
        const float kAutosaveSeconds = 120f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureRoot()
        {
            if (FindAnyObjectByType<GameRoot>() == null) new GameObject("GameRoot").AddComponent<GameRoot>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            var vt = Resources.Load<TextAsset>("version");
            if (vt != null) Version = vt.text.Trim();
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Debug.Log($"WM_BOOT version={Version} platform={Application.platform} gfx={SystemInfo.graphicsDeviceType} " +
                      $"gpu='{SystemInfo.graphicsDeviceName}' cpu={SystemInfo.processorCount} mem={SystemInfo.systemMemorySize}MB");

            GameSettings.Load();
            var preset = Cli.Str("preset");
            if (preset != null && Enum.TryParse(preset, true, out GraphicsPreset gp)) GameSettings.Current.ApplyPreset(gp);
            if (Cli.Has("viewdist")) GameSettings.Current.ViewDistance = (int)Cli.Float("viewdist", 10);
            NoSave = Cli.Has("nosave");
            if (Cli.Has("novsync")) { GameSettings.Current.VSync = false; GameSettings.Current.FpsCap = 0; }
            // Command-line overrides are for this session only: never write them into settings.json.
            GameSettings.SessionOnly = preset != null || Cli.Has("viewdist") || Cli.Has("novsync");

            Atmos = new GameObject("Atmosphere").AddComponent<Atmosphere>();
            Atmos.transform.SetParent(transform, false);
            gameObject.AddComponent<GraphicsApplier>();
            gameObject.AddComponent<Harness>();
            gameObject.AddComponent<Updater>();
            if (FindAnyObjectByType<PropRenderer>() == null) new GameObject("PropRenderer").AddComponent<PropRenderer>().transform.SetParent(transform, false);
        }

        void Start()
        {
            GraphicsApplier.Apply();
            if (Harness.WantsAutoStart) return; // the harness drives the flow
            if (Cli.Has("world") || Cli.Has("seed")) StartFromCli();
            else SetState(GameState.MainMenu);
        }

        void OnApplicationQuit()
        {
            if (State == GameState.Playing || State == GameState.Paused) SaveWorld();
            // A downloaded update installs after the game exits (the helper waits for this process).
            Updater.Instance?.InstallOnQuit();
        }

        public void StartFromCli()
        {
            string name = Cli.Str("world", "Cli World");
            long seed = Cli.Long("seed", 1337);
            WorldSave existing = null;
            if (!Cli.Has("new"))
                foreach (var w in WorldSave.List())
                    if (string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) { existing = w; break; }
            if (existing != null) LoadWorld(existing);
            else NewWorld(name, seed);
        }

        void SetState(GameState s)
        {
            if (State == s) return;
            State = s;
            ApplyCursor();
            Time.timeScale = s == GameState.Paused ? 0f : 1f;
            Debug.Log($"WM_STATE {s}");
            // Menus and pause run at a reduced rate so an idle laptop stays cool; play restores the user's settings.
            if (s == GameState.Paused || s == GameState.MainMenu)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = s == GameState.Paused ? 30 : 45;
            }
            else GraphicsApplier.Apply();
            if (s == GameState.MainMenu) MenuBackdrop.Show();
            else if (s == GameState.Loading) MenuBackdrop.Hide();
            StateChanged?.Invoke(s);
        }

        /// <summary>True while a UI screen (inventory, crafting, chat) owns the mouse during play.</summary>
        public bool UiCapture
        {
            get => mUiCapture;
            set { mUiCapture = value; ApplyCursor(); }
        }

        void ApplyCursor()
        {
            bool play = State == GameState.Playing && !mUiCapture && !Harness.Headless;
            Cursor.lockState = play ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !play;
            if (Player != null) Player.InputEnabled = State == GameState.Playing && !mUiCapture && !Player.Dead;
            if (Interaction != null) Interaction.InputEnabled = State == GameState.Playing && !mUiCapture;
        }

        // ------------------------------------------------------------------ world lifecycle

        public static long SeedFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return DateTime.UtcNow.Ticks ^ 0x5DEECE66DL;
            if (long.TryParse(text.Trim(), out long v)) return v;
            long h = 1125899906842597L;
            foreach (char c in text) h = 31 * h + c;
            return h;
        }

        /// <summary>Game mode of the open world (from its settings; see WorldSave.Creative).</summary>
        public bool Creative { get; private set; }

        public void NewWorld(string name, long seed, Dictionary<string, object> worldSettings = null)
        {
            var save = WorldSave.Create(string.IsNullOrWhiteSpace(name) ? "New World" : name.Trim(), seed);
            if (worldSettings != null) save.Meta["settings"] = worldSettings;
            StartCoroutine(LoadRoutine(save, true));
        }

        public void LoadWorld(WorldSave save)
        {
            StartCoroutine(LoadRoutine(save, false));
        }

        IEnumerator LoadRoutine(WorldSave save, bool isNew)
        {
            CloseWorld(false);
            SetState(GameState.Loading);
            LoadProgress = 0f;
            yield return null;

            Save = save;
            try { if (!isNew) save.LoadMods(); }
            catch (Exception e) { Debug.LogError($"WM_SAVE could not read block changes: {e.Message}"); }

            var gen = new TerrainGenerator(save.Seed);
            // Measured on the M4 (4P+6E): 4 workers stream smoothly; 6–8 starve the main/render threads (60–130 ms spikes).
            int threads = (int)Cli.Float("threads", Mathf.Clamp(SystemInfo.processorCount / 2 - 1, 2, 4));
            World = VoxelWorld.Create(gen, NoSave ? null : save, threads);
            World.ViewDistance = GameSettings.Current.ViewDistance;
            World.FancyLeaves = GameSettings.Current.FancyLeaves;

            // Player placement: saved state or a fresh spawn.
            var pdata = Json.Obj(save.Meta, "player");
            Vector3 spawn;
            float yaw = 0, pitch = 0;
            var spawnPt = gen.FindSpawn();
            Vector3 worldSpawn = new Vector3(spawnPt.x + 0.5f, spawnPt.y, spawnPt.z + 0.5f);
            if (pdata != null)
            {
                spawn = new Vector3((float)Json.Num(pdata, "x"), (float)Json.Num(pdata, "y"), (float)Json.Num(pdata, "z"));
                yaw = (float)Json.Num(pdata, "yaw"); pitch = (float)Json.Num(pdata, "pitch");
            }
            else spawn = worldSpawn;

            World.Focus = spawn;
            Player = PlayerController.Create(spawn, yaw);
            Player.Pitch = pitch;
            Player.SpawnPoint = worldSpawn;
            Player.InputEnabled = false;
            if (pdata != null)
            {
                Player.Health = (float)Json.Num(pdata, "health", PlayerController.MaxHealth);
                Player.Air = (float)Json.Num(pdata, "air", PlayerController.MaxAir);
                Player.Flying = Json.Bool(pdata, "flying");
                Player.Inventory.FromJson(Json.Arr(pdata, "inventory"));
                Player.Inventory.Selected = Mathf.Clamp((int)Json.Num(pdata, "selected"), 0, Inventory.Hotbar - 1);
                var sp = Json.Obj(pdata, "spawn");
                if (sp != null) Player.SpawnPoint = new Vector3((float)Json.Num(sp, "x"), (float)Json.Num(sp, "y"), (float)Json.Num(sp, "z"));
            }
            else if (!save.Creative && Json.Bool(Json.Obj(save.Meta, "settings"), "starterKit", true)) StarterKit(Player.Inventory);
            Creative = save.Creative;
            Player.Creative = Creative;
            Player.Died += () => Debug.Log("WM_PLAYER died");

            var wild = new GameObject("Wildlife");
            wild.transform.SetParent(World.transform, false);
            wild.AddComponent<Wildlife.WildlifeManager>();

            Interaction = Player.gameObject.AddComponent<BlockInteraction>();
            Interaction.Player = Player;
            Interaction.StationUsed += st => OpenCrafting?.Invoke(st);

            Atmos.TimeOfDay = (float)Json.Num(save.Meta, "time", Cli.Float("time", 0.30f));
            if (Cli.Has("time")) Atmos.TimeOfDay = Cli.Float("time", 0.3f);
            PlayTime = (float)Json.Num(save.Meta, "playTime");
            // World options chosen on the New World screen (keys: gameMode "survival"|"creative", starterKit bool,
            // dayLengthMinutes number).
            Atmos.DayLengthSeconds = (float)Json.Num(Json.Obj(save.Meta, "settings"), "dayLengthMinutes", 20) * 60f;
            Debug.Log($"WM_WORLD load name='{save.Name}' seed={save.Seed} new={isNew} mode={(Creative ? "creative" : "survival")} mods={save.ModifiedBlocks} spawn={spawn}");

            // Stream until the area around the player is generated and meshed.
            float t0 = Time.realtimeSinceStartup;
            while (!World.ReadyAround(spawn, 2))
            {
                World.Focus = spawn;
                LoadProgress = World.LoadProgress(spawn, 2);
                if (Time.realtimeSinceStartup - t0 > 90f) { Debug.LogError("WM_FAIL world load timed out"); break; }
                yield return null;
            }

            // A fresh spawn (or a saved position inside a block, e.g. after a generator change) is lifted to the surface.
            var p = Player.transform.position;
            if (pdata == null || VoxelPhysics.Overlaps(World, new Vector3(p.x - 0.3f, p.y, p.z - 0.3f), new Vector3(p.x + 0.3f, p.y + 1.8f, p.z + 0.3f)))
            {
                int sy = World.SurfaceY(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
                if (sy > 0) Player.Teleport(new Vector3(p.x, sy + 1.01f, p.z));
            }
            Debug.Log($"WM_WORLD ready in {Time.realtimeSinceStartup - t0:F2}s chunks={World.Loaded} meshed={World.MeshedCount}");
            LoadProgress = 1f;
            mAutosaveTimer = kAutosaveSeconds;
            SetState(GameState.Playing);
            WorldReady?.Invoke();
            if (isNew && !NoSave) SaveWorld();
        }

        static void StarterKit(Inventory inv)
        {
            // A gentle start: enough to light the first night. Everything else is gathered.
            inv.Add(B.Torch, 4);
        }

        public void SaveWorld()
        {
            if (Save == null || Player == null || NoSave) return;
            try
            {
                bool dead = Player.Dead;
                var p = dead ? Player.SpawnPoint : Player.transform.position; // load lifts it to the surface
                Save.Meta["player"] = new Dictionary<string, object>
                {
                    { "x", (double)p.x }, { "y", (double)p.y }, { "z", (double)p.z },
                    { "yaw", (double)Player.Yaw }, { "pitch", (double)Player.Pitch },
                    { "health", dead ? (double)PlayerController.MaxHealth : (double)Player.Health },
                    { "air", dead ? (double)PlayerController.MaxAir : (double)Player.Air }, { "flying", Player.Flying },
                    { "selected", (double)Player.Inventory.Selected },
                    { "inventory", Player.Inventory.ToJson() },
                    { "spawn", new Dictionary<string, object> { { "x", (double)Player.SpawnPoint.x }, { "y", (double)Player.SpawnPoint.y }, { "z", (double)Player.SpawnPoint.z } } },
                };
                Save.Meta["time"] = (double)Atmos.TimeOfDay;
                Save.Meta["playTime"] = (double)PlayTime;
                Save.Meta["gameVersion"] = Version;
                Weather.WeatherState?.Invoke(Save.Meta);
                Save.Save();
                Debug.Log($"WM_SAVE ok world='{Save.Name}' blocks={Save.ModifiedBlocks} dir={Save.Dir}");
                Saved?.Invoke();
            }
            catch (Exception e) { Debug.LogError($"WM_SAVE failed: {e}"); }
        }

        /// <summary>Hooks for systems that persist extra world state (weather etc.).</summary>
        public static class Weather
        {
            public static Action<Dictionary<string, object>> WeatherState;
        }

        public void Pause()
        {
            if (State != GameState.Playing) return;
            SetState(GameState.Paused);
        }

        public void Resume()
        {
            if (State != GameState.Paused) return;
            SetState(GameState.Playing);
        }

        public void QuitToMenu()
        {
            if (State == GameState.Playing || State == GameState.Paused) SaveWorld();
            CloseWorld(true);
            SetState(GameState.MainMenu);
        }

        void CloseWorld(bool notify)
        {
            if (World == null && Player == null) return;
            if (notify) WorldClosing?.Invoke();
            foreach (var d in ItemDrop.All.ToArray()) if (d != null) Destroy(d.gameObject);
            if (Player != null) Destroy(Player.gameObject);
            if (World != null) Destroy(World.gameObject);
            Player = null; Interaction = null; World = null; Save = null;
            mUiCapture = false;
            Time.timeScale = 1f;
        }

        void Update()
        {
            if (World != null && Player != null)
            {
                World.Focus = Player.transform.position;
                World.FocusDir = Player.LookDir;
                Atmos.Underwater = Player.HeadUnderwater;
                Atmos.ViewDistanceMeters = World.ViewDistance * 16f;
                Atmos.Paused = State != GameState.Playing;
            }

            if (State == GameState.Playing)
            {
                PlayTime += Time.unscaledDeltaTime;
                if (GameSettings.Pressed(Control.Pause) && !mUiCapture && !Harness.Headless) Pause();
                if (!mUiCapture && Cursor.lockState != CursorLockMode.Locked && !Harness.Headless && Input.GetMouseButtonDown(0)) ApplyCursor();
                mAutosaveTimer -= Time.unscaledDeltaTime;
                if (mAutosaveTimer <= 0) { mAutosaveTimer = kAutosaveSeconds; SaveWorld(); }
            }
            else if (State == GameState.Paused && GameSettings.Pressed(Control.Pause) && !mUiCapture) Resume();
        }
    }
}
