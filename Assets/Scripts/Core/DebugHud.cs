using System.Collections.Generic;
using UnityEngine;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Core
{
    /// <summary>
    /// Developer overlay (F3): FPS, position, chunk/worker stats. Until the UI module registers its
    /// real interface (<see cref="RealUiPresent"/>), it also provides a bare-bones menu, HUD, pause and
    /// inventory/crafting screen so every flow is testable.
    /// </summary>
    public sealed class DebugHud : MonoBehaviour
    {
        public static bool RealUiPresent;
        bool mShowDebug;
        bool mInventory;
        string mWorldName = "My World", mSeed = "";
        float mFps, mFpsTimer;
        int mFrames;
        Vector2 mScroll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var go = new GameObject("DebugHud");
            DontDestroyOnLoad(go);
            go.AddComponent<DebugHud>();
        }

        void Update()
        {
            mFrames++;
            mFpsTimer += Time.unscaledDeltaTime;
            if (mFpsTimer >= 0.5f) { mFps = mFrames / mFpsTimer; mFrames = 0; mFpsTimer = 0; }
            if (Input.GetKeyDown(KeyCode.F3)) mShowDebug = !mShowDebug;

            var root = GameRoot.Instance;
            if (RealUiPresent || root == null) return;
            if (root.State == GameState.Playing && GameSettings.Pressed(Control.Inventory))
            {
                mInventory = !mInventory;
                root.UiCapture = mInventory;
            }
            if (mInventory && root.State != GameState.Playing) mInventory = false;
        }

        void OnGUI()
        {
            var root = GameRoot.Instance;
            if (root == null) return;
            GUI.skin.label.fontSize = 13;
            if (mShowDebug) DebugText(root);
            if (RealUiPresent) return;

            switch (root.State)
            {
                case GameState.MainMenu: MainMenu(root); break;
                case GameState.Loading:
                    GUI.Label(new Rect(Screen.width / 2 - 100, Screen.height / 2, 300, 30), $"Generating world… {root.LoadProgress * 100f:F0}%");
                    break;
                case GameState.Playing: Hud(root); if (mInventory) InventoryScreen(root); break;
                case GameState.Paused: Hud(root); PauseMenu(root); break;
            }
        }

        void DebugText(GameRoot root)
        {
            var w = VoxelWorld.Instance;
            var p = root.Player;
            string s = $"Wildmere {GameRoot.Version}  {mFps:F0} fps  {SystemInfo.graphicsDeviceType}\n";
            if (p != null)
            {
                var pos = p.transform.position;
                var b = w != null ? w.Gen.SampleBiome(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z)) : default;
                s += $"XYZ {pos.x:F1} {pos.y:F1} {pos.z:F1}  yaw {p.Yaw:F0} pitch {p.Pitch:F0}\nbiome {b.Biome} t={b.Temperature:F2} h={b.Humidity:F2}  hp {p.Health:F0} air {p.Air:F0}\n";
            }
            if (w != null)
                s += $"chunks {w.Loaded} meshed {w.MeshedCount} gen {w.GenInFlight} mesh {w.MeshInFlight} jobs {w.Pool.Pending} busy {w.Pool.Busy}\nverts {w.TotalVertices / 1000}k view {w.ViewDistance}\n";
            s += $"time {Rendering.Atmosphere.Instance?.TimeOfDay:F3}";
            GUI.color = Color.black;
            GUI.Label(new Rect(11, 11, 900, 200), s);
            GUI.color = Color.white;
            GUI.Label(new Rect(10, 10, 900, 200), s);
        }

        void MainMenu(GameRoot root)
        {
            var r = new Rect(Screen.width / 2 - 170, Screen.height / 2 - 170, 340, 360);
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label("WILDMERE  (developer menu)");
            GUILayout.Label("World name"); mWorldName = GUILayout.TextField(mWorldName);
            GUILayout.Label("Seed (blank = random)"); mSeed = GUILayout.TextField(mSeed);
            if (GUILayout.Button("New World")) root.NewWorld(mWorldName, GameRoot.SeedFromText(mSeed));
            GUILayout.Space(8);
            mScroll = GUILayout.BeginScrollView(mScroll, GUILayout.Height(120));
            foreach (var w in WorldSave.List())
                if (GUILayout.Button($"Load: {w.Name}")) root.LoadWorld(w);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Quit")) Application.Quit();
            GUILayout.EndArea();
        }

        void Hud(GameRoot root)
        {
            var p = root.Player;
            if (p == null) return;
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            GUI.Label(new Rect(cx - 5, cy - 11, 20, 20), "+");
            var inv = p.Inventory;
            float slot = 52, x0 = cx - slot * 4.5f, y0 = Screen.height - slot - 12;
            for (int i = 0; i < Inventory.Hotbar; i++)
            {
                var s = inv.Slots[i];
                var rect = new Rect(x0 + i * slot, y0, slot - 4, slot - 4);
                GUI.Box(rect, i == inv.Selected ? "▣" : "");
                if (!s.IsEmpty) GUI.Label(new Rect(rect.x + 3, rect.y + 3, slot, slot), $"{Short(ItemDb.NameOf(s.Id))}\n{(s.Count > 1 ? s.Count.ToString() : "")}");
            }
            GUI.Label(new Rect(x0, y0 - 22, 400, 20), $"Health {p.Health:F0}/20" + (p.Air < PlayerController_MaxAir ? $"   Air {p.Air:F0}" : ""));
            var bi = root.Interaction;
            if (bi != null && bi.Target.Hit) GUI.Label(new Rect(cx + 20, cy + 10, 300, 20), Blocks.Get(bi.Target.Id).Name + (bi.MineProgress > 0 ? $"  {bi.MineProgress * 100:F0}%" : ""));
            if (p.Dead)
            {
                GUILayout.BeginArea(new Rect(cx - 120, cy - 60, 240, 120), GUI.skin.box);
                GUILayout.Label("You died.");
                if (GUILayout.Button("Respawn")) { p.Respawn(); root.UiCapture = false; }
                GUILayout.EndArea();
            }
        }

        const float PlayerController_MaxAir = 10f;
        static string Short(string n) => n.Length > 9 ? n.Substring(0, 9) : n;

        void PauseMenu(GameRoot root)
        {
            GUILayout.BeginArea(new Rect(Screen.width / 2 - 120, Screen.height / 2 - 90, 240, 180), GUI.skin.box);
            GUILayout.Label("Paused");
            if (GUILayout.Button("Resume")) root.Resume();
            if (GUILayout.Button("Save")) root.SaveWorld();
            if (GUILayout.Button("Save & Quit to Menu")) root.QuitToMenu();
            GUILayout.EndArea();
        }

        void InventoryScreen(GameRoot root)
        {
            var p = root.Player;
            var inv = p.Inventory;
            var near = Crafting.StationsNear(root.World, p.transform.position);
            GUILayout.BeginArea(new Rect(Screen.width / 2 - 360, 60, 720, Screen.height - 180), GUI.skin.box);
            GUILayout.Label($"Inventory (E to close)   stations nearby: {string.Join(", ", near)}");
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(330));
            for (int i = 0; i < Inventory.Size; i++)
            {
                var s = inv.Slots[i];
                if (s.IsEmpty) continue;
                GUILayout.Label($"[{i}] {ItemDb.NameOf(s.Id)} x{s.Count}" + (s.Def?.Durability > 0 ? $" ({s.Durability})" : ""));
            }
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            mScroll = GUILayout.BeginScrollView(mScroll);
            foreach (var r in Crafting.Recipes)
            {
                bool can = Crafting.CanCraft(inv, r, near);
                var parts = new List<string>();
                foreach (var (id, c) in r.Inputs) parts.Add($"{c} {ItemDb.NameOf(id)}");
                GUI.enabled = can;
                if (GUILayout.Button($"{ItemDb.NameOf(r.Output)} x{r.OutputCount} ← {string.Join(" + ", parts)}" + (r.Station != Station.None ? $" [{r.Station}]" : "")))
                    Crafting.Craft(inv, r, near);
                GUI.enabled = true;
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
