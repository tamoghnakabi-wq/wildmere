using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.Player;
using Wildmere.World;

namespace Wildmere.UI
{
    /// <summary>
    /// The Wildmere interface (UI Toolkit, built entirely from code + Resources/UI/Wildmere.uss).
    /// Owns the panel, the screen stack, Esc/back routing and cursor ownership via
    /// <see cref="GameRoot.UiCapture"/>. Runs after GameRoot each frame (execution order 100) so the
    /// pause key never double-fires between them.
    ///
    /// Backdrop hook: <see cref="SetBackdropActive"/> switches the main menu between the animated
    /// fallback background (false) and a translucent overlay over the live world (true). By default it
    /// follows <see cref="MenuBackdrop"/> automatically once that world has meshed around its camera.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class UiRoot : MonoBehaviour
    {
        public static UiRoot Instance { get; private set; }

        /// <summary>When true (default) the backdrop state follows MenuBackdrop automatically.</summary>
        public static bool AutoBackdrop = true;

        public VisualElement Root { get; private set; }
        public Hud Hud { get; private set; }
        public MainMenuScreen MainMenu { get; private set; }
        public NewWorldScreen NewWorld { get; private set; }
        public LoadWorldScreen LoadWorld { get; private set; }
        public SettingsScreen Settings { get; private set; }
        public PauseScreen Pause { get; private set; }
        public LoadingScreen Loading { get; private set; }
        public DeathScreen Death { get; private set; }
        public InventoryScreen Inventory { get; private set; }
        public ConfirmDialog Dialog { get; private set; }

        public UiScreen Top => mStack.Count > 0 ? mStack[mStack.Count - 1] : null;
        public IReadOnlyList<UiScreen> Stack => mStack;
        public bool BackdropActive { get; private set; }
        public bool Ready { get; private set; }

        readonly List<UiScreen> mStack = new List<UiScreen>();
        readonly List<UiScreen> mScreens = new List<UiScreen>();
        UIDocument mDoc;
        AnimatedBackground mBackground;
        GameRoot mGame;
        GameState mState = GameState.Boot;
        int mStateFrame = -1;
        bool mRepause;
        Vector3 mLastMouse;
        float mBackdropReadyTime = -1f;
        PlayerController mHookedPlayer;
        BlockInteraction mHookedInteraction;
        public int EscConsumedFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject("UiRoot");
            DontDestroyOnLoad(go);
            go.AddComponent<UiRoot>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DebugHud.RealUiPresent = true;

            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = "WildmerePanel";
            ps.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/WildmereTheme");
            if (ps.themeStyleSheet == null) Debug.LogWarning("WM_UI theme missing (Resources/UI/WildmereTheme.tss)");
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 1f;
            ps.sortingOrder = 10;
            ps.clearColor = false;
            mDoc = gameObject.AddComponent<UIDocument>();
            mDoc.panelSettings = ps;

            Root = new VisualElement { name = "wildmere-ui" };
            Root.AddToClassList("wm");
            Root.pickingMode = PickingMode.Ignore;
            mDoc.rootVisualElement.Add(Root);
            mDoc.rootVisualElement.pickingMode = PickingMode.Ignore;

            mBackground = new AnimatedBackground();
            Root.Add(mBackground.Root);
            Hud = new Hud();
            Root.Add(Hud.Root);

            MainMenu = Register(new MainMenuScreen());
            NewWorld = Register(new NewWorldScreen());
            LoadWorld = Register(new LoadWorldScreen());
            Settings = Register(new SettingsScreen());
            Pause = Register(new PauseScreen());
            Loading = Register(new LoadingScreen());
            Death = Register(new DeathScreen());
            Inventory = Register(new InventoryScreen());
            Dialog = Register(new ConfirmDialog());

            Application.logMessageReceived += OnLog;
        }

        T Register<T>(T s) where T : UiScreen
        {
            s.Build();
            Root.Add(s.Root);
            mScreens.Add(s);
            return s;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            if (mGame != null)
            {
                mGame.StateChanged -= OnState;
                mGame.WorldReady -= OnWorldReady;
                mGame.WorldClosing -= OnWorldClosing;
                mGame.OpenCrafting -= OnOpenCrafting;
            }
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            ItemIcons.Build();
            TryHookGame();
            if (UiHarness.Active) gameObject.AddComponent<UiHarness>();
        }

        void TryHookGame()
        {
            if (mGame != null || GameRoot.Instance == null) return;
            mGame = GameRoot.Instance;
            mGame.StateChanged += OnState;
            mGame.WorldReady += OnWorldReady;
            mGame.WorldClosing += OnWorldClosing;
            mGame.OpenCrafting += OnOpenCrafting;
            Ready = true;
            Debug.Log($"WM_UI ready screens={mScreens.Count} theme={(mDoc.panelSettings.themeStyleSheet != null)} icons={ItemIcons.Count}");
            if (mGame.State != GameState.Boot) OnState(mGame.State);
            if (mGame.Player != null) OnWorldReady();
        }

        // ------------------------------------------------------------------ stack

        public void Push(UiScreen s, bool keepBelow = false)
        {
            if (s == null) return;
            if (mStack.Contains(s)) { if (Top == s) return; mStack.Remove(s); }
            if (!keepBelow && Top != null) Top.Hide();
            mStack.Add(s);
            s.Show();
            UiSound.Play(UiSound.Open);
        }

        public void Pop(UiScreen s = null)
        {
            if (mStack.Count == 0) return;
            s ??= Top;
            int i = mStack.IndexOf(s);
            if (i < 0) return;
            bool wasTop = i == mStack.Count - 1;
            mStack.RemoveAt(i);
            s.Hide();
            if (wasTop && Top != null && !Top.IsOpen) Top.Show();
            UiSound.Play(UiSound.Close);
        }

        public void ClearStack()
        {
            foreach (var s in mStack) s.Hide();
            mStack.Clear();
        }

        /// <summary>Replace the whole stack with one base screen.</summary>
        void SetBase(UiScreen s)
        {
            foreach (var o in mStack) if (o != s) o.Hide();
            mStack.Clear();
            if (s != null) { mStack.Add(s); s.Show(); }
        }

        // ------------------------------------------------------------------ actions used by screens

        public GameRoot Game => mGame;

        public void PlayWorld(WorldSave w)
        {
            if (mGame == null || w == null) return;
            UiSound.Play(UiSound.Confirm);
            mGame.LoadWorld(w);
        }

        public void Confirm(string title, string message, string yes, bool danger, Action onYes)
        {
            Dialog.Setup(title, message, yes, danger, onYes);
            Push(Dialog, keepBelow: true);
        }

        public void OpenInventory(bool crafting, Station station = Station.None)
        {
            if (mGame == null || mGame.State != GameState.Playing || mGame.Player == null || mGame.Player.Dead) return;
            Inventory.Prepare(crafting, station);
            if (Top != Inventory) Push(Inventory);
        }

        // ------------------------------------------------------------------ game events

        void OnState(GameState s)
        {
            var prev = mState;
            mState = s;
            mStateFrame = Time.frameCount;
            switch (s)
            {
                case GameState.MainMenu:
                    mRepause = false;
                    Hud.SetVisible(false);
                    SetBase(MainMenu);
                    mBackdropReadyTime = -1f;
                    SetBackground(true);
                    SetBackdropActive(false, instant: true);
                    break;
                case GameState.Loading:
                    Hud.SetVisible(false);
                    SetBase(Loading);
                    SetBackground(true);
                    SetBackdropActive(false);
                    break;
                case GameState.Playing:
                    SetBackground(false);
                    if (prev == GameState.Paused && PauseSubscreenOpen && EscDown)
                    {
                        // GameRoot resumed on Esc while a pause sub-screen was open: step back instead.
                        mRepause = true;
                        Pop(Top);
                        break;
                    }
                    if (prev == GameState.Paused || prev == GameState.Loading) SetBase(null);
                    Hud.SetVisible(true);
                    break;
                case GameState.Paused:
                    if (!mStack.Contains(Pause)) Push(Pause, keepBelow: true);
                    break;
            }
        }

        bool PauseSubscreenOpen => mStack.Count > 1 && mStack[0] == Pause;
        static bool EscDown => Input.GetKeyDown(KeyCode.Escape) || GameSettings.Pressed(Control.Pause);

        void OnWorldReady()
        {
            UnhookPlayer();
            var p = mGame.Player;
            if (p == null) return;
            mHookedPlayer = p;
            p.Damaged += OnDamaged;
            p.Died += OnDied;
            mHookedInteraction = mGame.Interaction;
            if (mHookedInteraction != null) mHookedInteraction.ToolBroke += OnToolBroke;
            ItemDrop.PickedUp -= OnPickedUp;
            ItemDrop.PickedUp += OnPickedUp;
            Hud.Bind(p);
            Inventory.Bind(p);
        }

        void OnWorldClosing()
        {
            UnhookPlayer();
            Hud.Bind(null);
            Inventory.Bind(null);
        }

        void UnhookPlayer()
        {
            if (mHookedPlayer != null) { mHookedPlayer.Damaged -= OnDamaged; mHookedPlayer.Died -= OnDied; }
            if (mHookedInteraction != null) mHookedInteraction.ToolBroke -= OnToolBroke;
            ItemDrop.PickedUp -= OnPickedUp;
            mHookedPlayer = null; mHookedInteraction = null;
        }

        void OnDamaged(float amount, string cause)
        {
            Hud.Damage(amount);
            Death.LastCause = cause;
        }

        void OnDied()
        {
            if (Top == Inventory) Pop(Inventory);
            Push(Death);
        }

        void OnToolBroke(ItemDef d) => Hud.Toast(d != null ? d.Id : 0, 0, $"{d?.Name ?? "Tool"} broke", warn: true);

        void OnPickedUp(ItemDrop d)
        {
            if (d == null) return;
            Hud.NotePickup(d.Stack.Id);
        }

        void OnOpenCrafting(Station st) => OpenInventory(true, st);

        void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Log && msg.StartsWith("WM_SAVE ok", StringComparison.Ordinal)) Hud?.ShowSaved();
        }

        // ------------------------------------------------------------------ backdrop

        /// <summary>
        /// true: the live world backdrop is showing, so the main menu becomes a translucent overlay.
        /// false: show the animated fallback background.
        /// </summary>
        public void SetBackdropActive(bool active) => SetBackdropActive(active, false);

        void SetBackdropActive(bool active, bool instant)
        {
            BackdropActive = active;
            mBackground.Root.EnableInClassList("bg-anim--off", active);
            if (instant) mBackground.Root.style.opacity = StyleKeyword.Null;
        }

        void SetBackground(bool on)
        {
            mBackground.Root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void UpdateBackdrop()
        {
            if (!AutoBackdrop || mState != GameState.MainMenu) return;
            var w = VoxelWorld.Instance;
            bool live = MenuBackdrop.Instance != null && w != null && w.ReadyAround(w.Focus, 3);
            if (live && mBackdropReadyTime < 0) mBackdropReadyTime = Time.unscaledTime;
            if (!live) mBackdropReadyTime = -1f;
            bool want = live && Time.unscaledTime - mBackdropReadyTime > 0.6f;
            if (want != BackdropActive) SetBackdropActive(want);
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            TryHookGame();
            if (mGame == null) return;
            float dt = Time.unscaledDeltaTime;

            if (mRepause)
            {
                mRepause = false;
                if (mGame.State == GameState.Playing) mGame.Pause();
            }

            // Keyboard-navigation focus rings only after the keyboard is used.
            var mp = Input.mousePosition;
            if ((mp - mLastMouse).sqrMagnitude > 4f) { Root.RemoveFromClassList("kbd"); mLastMouse = mp; }
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow) ||
                Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow)) Root.AddToClassList("kbd");

            HandleInput();

            foreach (var s in mStack) if (s.IsOpen) s.Tick(dt);
            Hud.Tick(dt, mGame);
            mBackground.Tick(dt);
            UpdateBackdrop();
            ApplyCapture();
        }

        void HandleInput()
        {
            var state = mGame.State;
            if (EscDown && mStateFrame != Time.frameCount && EscConsumedFrame != Time.frameCount && !mRepause)
            {
                var top = Top;
                if (top != null) top.OnBack();
            }
            if (state == GameState.Playing && mGame.Player != null)
            {
                if (GameSettings.Pressed(Control.Inventory))
                {
                    if (Top == Inventory) Pop(Inventory);
                    else if (mStack.Count == 0 && !mGame.Player.Dead) OpenInventory(false);
                }
                if (GameSettings.Pressed(Control.ToggleHud) && mStack.Count == 0) Hud.ToggleHidden();
            }
        }

        /// <summary>Esc / back, as if the key was pressed (used by tests).</summary>
        public void Back()
        {
            var top = Top;
            if (top != null) top.OnBack();
            ApplyCapture();
        }

        void ApplyCapture()
        {
            var state = mGame.State;
            bool want = false;
            if (state == GameState.Playing) foreach (var s in mStack) want |= s.CapturesMouse;
            if (state == GameState.Paused) want = PauseSubscreenOpen;
            if (mGame.UiCapture != want) mGame.UiCapture = want;
        }
    }
}
