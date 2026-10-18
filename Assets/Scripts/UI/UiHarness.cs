using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.UI
{
    /// <summary>
    /// UI-only harness flags for the shipping binary:
    ///   -uishot all|mainmenu,newworld,load,dialog,settings-graphics,settings-audio,settings-controls,
    ///           loading,hud,inventory,crafting,pause,death   [-shotdir DIR]
    ///      opens each screen with representative state and writes ui_&lt;name&gt;.png, then quits.
    ///   -uitest   scripted interaction checks through the real screens; prints WM_UITEST PASS/FAIL
    ///      lines and a summary, exit code 0 = pass.
    /// Use with -nosave so the test world is never written. Temporary saved worlds created for the
    /// Load screen are tagged and deleted again. Player settings are restored afterwards.
    /// </summary>
    public sealed class UiHarness : MonoBehaviour
    {
        public static bool Active => Cli.Has("uishot") || Cli.Has("uitest");
        UiRoot UI => UiRoot.Instance;
        GameRoot Game => GameRoot.Instance;
        int mPass, mFail;
        string mSettingsBackup;

        void Start()
        {
            mSettingsBackup = JsonUtility.ToJson(GameSettings.Current);
            if (Cli.Has("uitest")) StartCoroutine(Test());
            else StartCoroutine(Shots());
        }

        static IEnumerator Wait(float s)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < s) yield return null;
        }

        IEnumerator WaitFor(Func<bool> cond, float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!cond() && Time.realtimeSinceStartup - t0 < timeout) yield return null;
        }

        // ------------------------------------------------------------------ layout assertions

        static bool Shown(VisualElement e)
        {
            for (var x = e; x != null; x = x.parent)
                if (x.resolvedStyle.display == DisplayStyle.None || x.resolvedStyle.opacity < 0.01f && x.ClassListContains("screen")) return false;
            return e.resolvedStyle.visibility == Visibility.Visible;
        }

        static string R(Rect r) => $"({r.x:F0},{r.y:F0} {r.width:F0}x{r.height:F0})";

        /// <summary>
        /// Null when the scope lays out sanely: the panel root fills the screen, every element with the
        /// given classes is visible, non-degenerate and fully on screen, and no single-line label has
        /// collapsed into a column of letters. Otherwise a short description of the problems.
        /// </summary>
        string LayoutIssues(VisualElement scope, params string[] classes)
        {
            var root = UI.Root.worldBound;
            var issues = new List<string>();
            if (root.width < 600 || root.height < 400) issues.Add($"root {R(root)}");
            foreach (var c in classes)
            {
                var list = scope.Query<VisualElement>(className: c).ToList().Where(Shown).ToList();
                if (list.Count == 0) { issues.Add($".{c} not visible"); continue; }
                var seen = new HashSet<VisualElement>();
                foreach (var e0 in list)
                {
                    // Rows inside a scroll view are legitimately clipped: check the scroll view instead.
                    var e = e0;
                    for (var x = e0.parent; x != null; x = x.parent) if (x is ScrollView) { e = x; break; }
                    if (!seen.Add(e)) continue;
                    var r = e.worldBound;
                    if (r.width < 8 || r.height < 8 || r.xMin < root.xMin - 1 || r.yMin < root.yMin - 1 || r.xMax > root.xMax + 1 || r.yMax > root.yMax + 1)
                        issues.Add($".{c} {R(r)} outside {R(root)}");
                }
            }
            foreach (var l in scope.Query<Label>().ToList())
            {
                if (string.IsNullOrEmpty(l.text) || !Shown(l) || l.resolvedStyle.whiteSpace != WhiteSpace.NoWrap) continue;
                if (l.worldBound.height > l.resolvedStyle.fontSize * 2.6f) issues.Add($"label '{l.text}' is {l.worldBound.height:F0}px tall");
            }
            return issues.Count == 0 ? null : string.Join("; ", issues.Take(5));
        }

        void CheckLayout(string name, VisualElement scope, params string[] classes)
        {
            var issues = LayoutIssues(scope, classes);
            Check("layout_" + name, issues == null, issues ?? $"screen={Screen.width}x{Screen.height} panel={R(UI.Root.worldBound)}");
        }

        void LogLayout(string name, VisualElement scope, params string[] classes)
        {
            var issues = LayoutIssues(scope, classes);
            Debug.Log($"WM_UILAYOUT {name} {(issues == null ? "OK" : "BAD " + issues)}");
        }

        void RestoreSettings()
        {
            GameSettings.Current.ResetBindings();
            JsonUtility.FromJsonOverwrite(mSettingsBackup, GameSettings.Current);
            GameSettings.Save();
        }

        static void CleanupTempWorlds()
        {
            foreach (var w in WorldSave.List())
                if (Json.Bool(w.Meta, "uiHarness")) { try { w.Delete(); } catch (Exception e) { Debug.LogWarning($"WM_UI cleanup: {e.Message}"); } }
        }

        static WorldSave TempWorld(string name, long seed, double playTime, double hoursAgo)
        {
            var w = WorldSave.Create(name, seed);
            w.Meta["uiHarness"] = true;
            w.Meta["playTime"] = playTime;
            w.Save();
            w.Meta["lastPlayed"] = DateTime.UtcNow.AddHours(-hoursAgo).ToString("o");
            File.WriteAllText(Path.Combine(w.Dir, "world.json"), Json.Write(w.Meta, true));
            return w;
        }

        void GiveKit()
        {
            var p = Game.Player;
            var inv = p.Inventory;
            inv.Clear();
            void Put(int slot, int id, int count, float dur = 1f)
            {
                var s = ItemStack.Of(id, count);
                if (s.Def != null && s.Def.Durability > 0) s.Durability = Mathf.Max(1, Mathf.RoundToInt(s.Def.Durability * dur));
                inv.Slots[slot] = s;
            }
            Put(0, ItemIds.PickaxeIron, 1, 0.62f); Put(1, ItemIds.AxeStone, 1, 0.3f); Put(2, B.Torch, 16); Put(3, B.Cobblestone, 48);
            Put(4, B.Planks, 32); Put(5, B.LogOak, 12); Put(6, B.Glass, 8); Put(7, B.Grass, 64); Put(8, ItemIds.ShovelWood, 1);
            Put(9, B.Stone, 64); Put(10, B.Dirt, 37); Put(11, B.Sand, 20); Put(12, ItemIds.Coal, 9); Put(13, ItemIds.RawIron, 4);
            Put(14, ItemIds.Stick, 14); Put(15, B.LeavesOak, 6); Put(16, B.FlowerRed, 3); Put(18, B.Workbench, 1); Put(19, B.Furnace, 1);
            Put(20, ItemIds.IronIngot, 6); Put(21, B.LogBirch, 9); Put(23, B.MossyCobblestone, 18); Put(24, B.Gravel, 11);
            Put(27, ItemIds.Flint, 2); Put(28, B.GlowCap, 4); Put(30, B.Sandstone, 24); Put(33, ItemIds.GoldIngot, 2);
            inv.Selected = 0;
            inv.Notify();
        }

        // ------------------------------------------------------------------ screenshots

        IEnumerator Shots()
        {
            string dir = Cli.Str("shotdir", Path.Combine(Application.persistentDataPath, "Screenshots", "ui"));
            Directory.CreateDirectory(dir);
            var want = new HashSet<string>((Cli.Str("uishot", "all")).ToLowerInvariant().Split(','));
            bool All(string s) => want.Contains("all") || want.Contains(s);
            yield return WaitFor(() => UI != null && UI.Ready && Game != null && Game.State == GameState.MainMenu, 20f);

            CleanupTempWorlds();
            if (All("load") || All("dialog") || All("mainmenu"))
            {
                TempWorld("Willow Hollow", 82731, 3 * 3600 + 14 * 60, 2);
                TempWorld("Ashen Peaks", 1337, 47 * 60, 26);
                TempWorld("Mistral Bay", 99120345, 11 * 3600 + 5 * 60, 24 * 9);
                UI.MainMenu.Hide(true);
                UI.MainMenu.Show(); // rebuild with the Continue entry
            }

            IEnumerator Shot(string name, float settle = 0.7f)
            {
                yield return Wait(settle);
                yield return new WaitForEndOfFrame();
                string path = Path.Combine(dir, $"ui_{name}.png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"WM_UISHOT {name} {path}");
                yield return null;
            }

            if (All("mainmenu"))
            {
                yield return WaitFor(() => UI.BackdropActive, 25f);
                yield return Shot("mainmenu", 2.2f);
                LogLayout("mainmenu", UI.MainMenu.Root, "mm-column", "wordmark", "menu-item", "mm-foot");
            }
            if (All("newworld")) { UI.Push(UI.NewWorld); yield return Shot("newworld"); LogLayout("newworld", UI.NewWorld.Root, "panel", "wm-input", "choice"); UI.Pop(UI.NewWorld); }
            if (All("load")) { UI.Push(UI.LoadWorld); yield return Shot("load"); LogLayout("load", UI.LoadWorld.Root, "panel", "world-card"); }
            if (All("dialog")) { if (UI.Top != UI.LoadWorld) UI.Push(UI.LoadWorld); yield return Wait(0.3f); UI.LoadWorld.AskDelete(); yield return Shot("dialog"); UI.Pop(UI.Dialog); }
            if (UI.Top == UI.LoadWorld) UI.Pop(UI.LoadWorld);
            foreach (var (tab, key) in new[] { (SettingsScreen.Tab.Graphics, "settings-graphics"), (SettingsScreen.Tab.Audio, "settings-audio"), (SettingsScreen.Tab.Controls, "settings-controls") })
            {
                if (!All(key)) continue;
                if (UI.Top != UI.Settings) UI.Push(UI.Settings);
                UI.Settings.Show(tab);
                yield return Shot(key);
                LogLayout(key, UI.Settings.Root, "panel", "tabs", "setting-row");
            }
            if (UI.Top == UI.Settings) UI.Pop(UI.Settings);
            CleanupTempWorlds();

            bool game = All("loading") || All("hud") || All("inventory") || All("crafting") || All("pause") || All("death");
            if (game)
            {
                UI.Push(UI.NewWorld);
                yield return Wait(0.3f);
                UI.NewWorld.NameField.value = "Shot World";
                UI.NewWorld.SeedField.value = Cli.Str("shotseed", "20261018");
                UI.NewWorld.Create();
                if (All("loading")) { yield return WaitFor(() => Game.LoadProgress > 0.3f, 30f); yield return Shot("loading", 0.05f); }
                yield return WaitFor(() => Game.State == GameState.Playing, 120f);
                if (Game.Save != null) Game.Save.Meta["uiHarness"] = true;
                yield return Wait(1.5f);
                GiveKit();
                var p = Game.Player;
                p.Pitch = 38f;
                p.Health = 13f;

                if (All("hud"))
                {
                    yield return Wait(1.0f);
                    UI.Hud.Toast(B.LogOak, 3, null);
                    yield return Wait(0.25f);
                    UI.Hud.Toast(ItemIds.Coal, 2, null);
                    Game.Interaction.Select(3);
                    Game.Interaction.ScriptedAttack = true;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 0.45f) { p.Air = 6.4f; yield return null; }
                    p.Air = 6.4f;
                    yield return new WaitForEndOfFrame();
                    string path = Path.Combine(dir, "ui_hud.png");
                    ScreenCapture.CaptureScreenshot(path);
                    Debug.Log($"WM_UISHOT hud {path}");
                    yield return null;
                    Game.Interaction.ScriptedAttack = null;
                    UI.Hud.ShowSaved();
                    yield return Shot("hud-saved", 0.5f);
                }
                if (All("inventory"))
                {
                    UI.OpenInventory(false);
                    yield return Wait(0.4f);
                    UI.Inventory.Select(Crafting.Recipes.Find(r => r.Key == "torch"));
                    UI.Inventory.DebugHover(0);
                    yield return Shot("inventory");
                    LogLayout("inventory", UI.Inventory.Root, "inv-panel", "craft-panel", "inv-slot", "recipe", "craft-detail");
                    UI.Inventory.DebugHover(-1);
                    UI.Inventory.SlotDown(14, 1, false); // hold half the sticks over another slot
                    UI.Inventory.DebugHover(16);
                    yield return Shot("inventory-held", 0.4f);
                    UI.Inventory.DebugHover(-1);
                    UI.Back();
                    yield return Wait(0.4f);
                }
                if (All("crafting"))
                {
                    UI.OpenInventory(true, Station.Workbench);
                    UI.Inventory.SetCategory("Tools");
                    UI.Inventory.Select(Crafting.Recipes.Find(r => r.Key == "pickaxe_stone"));
                    yield return Shot("crafting");
                    UI.Back();
                    yield return Wait(0.4f);
                }
                if (All("pause"))
                {
                    Game.Pause();
                    yield return Shot("pause");
                    LogLayout("pause", UI.Pause.Root, "side-column", "menu-item");
                    UI.Push(UI.Settings);
                    UI.Settings.Show(SettingsScreen.Tab.Graphics);
                    yield return Shot("pause-settings");
                    UI.Back();
                    yield return Wait(0.3f);
                    Game.Resume();
                    yield return Wait(0.4f);
                }
                if (All("death"))
                {
                    p.Hurt(100f, "fall");
                    yield return Shot("death", 1.0f);
                }
            }
            if (Game.State == GameState.Playing || Game.State == GameState.Paused) Game.QuitToMenu();
            CleanupTempWorlds();
            RestoreSettings();
            Debug.Log("WM_UISHOT done");
            yield return Wait(0.5f);
            Quit(0);
        }

        // ------------------------------------------------------------------ scripted test

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) mPass++; else mFail++;
            Debug.Log($"WM_UITEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        IEnumerator Test()
        {
            yield return WaitFor(() => UI != null && UI.Ready && Game != null && Game.State == GameState.MainMenu, 20f);
            yield return Wait(0.5f);
            Check("mainmenu_shown", UI.Top == UI.MainMenu && UI.MainMenu.IsOpen, $"top={UI.Top?.Id}");
            CheckLayout("mainmenu", UI.MainMenu.Root, "mm-column", "wordmark", "menu-item", "mm-foot");
            Check("debug_hud_suppressed", DebugHud.RealUiPresent);
            Check("icons_rendered", SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || ItemIcons.Get(B.Grass) != null && ItemIcons.Get(ItemIds.PickaxeIron) != null,
                $"atlas={(ItemIcons.Atlas != null ? $"{ItemIcons.Atlas.width}x{ItemIcons.Atlas.height}" : "none")}");

            // Settings: preset, custom switch, rebinding, back navigation.
            UI.Push(UI.Settings);
            yield return Wait(0.3f);
            UI.Settings.Show(SettingsScreen.Tab.Graphics);
            yield return Wait(0.3f);
            CheckLayout("settings", UI.Settings.Root, "panel", "tabs", "setting-row", "choice", "slider");
            UI.Settings.PresetChoice.Pick((int)GraphicsPreset.Low);
            bool low = GameSettings.Current.Preset == GraphicsPreset.Low && GameSettings.Current.ViewDistance == 6;
            UI.Settings.ViewDistance.SetValue(9, true);
            UI.Settings.RefreshAll();
            yield return null;
            Check("settings_preset_and_custom", low && GameSettings.Current.Preset == GraphicsPreset.Custom && GameSettings.Current.ViewDistance == 9,
                $"preset={GameSettings.Current.Preset} view={GameSettings.Current.ViewDistance}");
            UI.Settings.Show(SettingsScreen.Tab.Controls);
            UI.Settings.BeginCapture(Control.Jump);
            bool capturing = UI.Settings.Capturing;
            UI.Settings.Bind(Control.Jump, KeyCode.E); // E is Inventory: the two must swap
            Check("rebind_swaps_conflict", capturing && GameSettings.Current.Key(Control.Jump) == KeyCode.E && GameSettings.Current.Key(Control.Inventory) == KeyCode.Space,
                $"jump={GameSettings.Current.Key(Control.Jump)} inv={GameSettings.Current.Key(Control.Inventory)}");
            GameSettings.Current.ResetBindings();
            UI.Back();
            yield return Wait(0.3f);
            Check("esc_backs_out_of_settings", UI.Top == UI.MainMenu, $"top={UI.Top?.Id}");
            RestoreSettings();

            // Load screen: list, delete with confirmation.
            CleanupTempWorlds();
            var temp = TempWorld("UI Test World", 4242, 125, 1);
            UI.Push(UI.LoadWorld);
            yield return Wait(0.3f);
            bool listed = false;
            foreach (var w in WorldSave.List()) if (w.Dir == temp.Dir) { UI.LoadWorld.Select(w); listed = true; }
            Check("load_lists_world", listed && UI.LoadWorld.WorldCount > 0, $"count={UI.LoadWorld.WorldCount}");
            CheckLayout("load", UI.LoadWorld.Root, "panel", "world-card");
            UI.LoadWorld.AskDelete();
            yield return Wait(0.2f);
            bool dialog = UI.Top == UI.Dialog;
            UI.Dialog.Yes();
            yield return Wait(0.2f);
            Check("delete_with_confirm", dialog && !Directory.Exists(temp.Dir) && UI.Top == UI.LoadWorld, $"dialog={dialog} exists={Directory.Exists(temp.Dir)}");
            CleanupTempWorlds();
            UI.Back();
            yield return Wait(0.3f);

            // New world through the form.
            UI.Push(UI.NewWorld);
            yield return Wait(0.3f);
            CheckLayout("newworld", UI.NewWorld.Root, "panel", "wm-input", "choice", "switch");
            UI.NewWorld.NameField.value = "UITest";
            UI.NewWorld.SeedField.value = "424242";
            UI.NewWorld.StarterKit.Value = false;
            UI.NewWorld.DayLength.Pick(2);
            var ws = UI.NewWorld.WorldSettings();
            UI.NewWorld.Create();
            yield return WaitFor(() => Game.State == GameState.Loading, 5f);
            Check("loading_screen", UI.Top == UI.Loading, $"top={UI.Top?.Id}");
            yield return WaitFor(() => Game.State == GameState.Playing, 120f);
            if (Game.Save != null) Game.Save.Meta["uiHarness"] = true;
            yield return Wait(1f);
            var p = Game.Player;
            Check("new_world_started", Game.State == GameState.Playing && p != null && Game.Save != null && Game.Save.Seed == 424242,
                $"seed={Game.Save?.Seed} settings={Json.Write(ws, false)}");
            Check("world_settings_keys", ws.ContainsKey("starterKit") && (bool)ws["starterKit"] == false && ws.ContainsKey("dayLengthMinutes") && (double)ws["dayLengthMinutes"] == 40.0);
            if (p == null) { Finish(); yield break; }
            Check("hud_visible", UI.Hud.Visible && UI.Stack.Count == 0);
            yield return Wait(0.3f);
            CheckLayout("hud", UI.Hud.Root, "hotbar", "slot", "health", "crosshair");

            // Inventory interactions.
            var inv = p.Inventory;
            inv.Clear();
            inv.Slots[0] = ItemStack.Of(B.LogOak, 3);
            inv.Slots[1] = ItemStack.Of(B.Stone, 10);
            inv.Notify();
            UI.OpenInventory(false);
            yield return Wait(0.3f);
            Check("inventory_opens_with_cursor", UI.Top == UI.Inventory && Game.UiCapture && UnityEngine.Cursor.lockState != CursorLockMode.Locked || UI.Top == UI.Inventory && Game.UiCapture && Harness.Headless,
                $"capture={Game.UiCapture} lock={UnityEngine.Cursor.lockState}");
            var iv = UI.Inventory;
            CheckLayout("inventory", iv.Root, "inv-panel", "craft-panel", "inv-slot", "recipe", "craft-detail");
            iv.SlotDown(0, 0, false); iv.SlotUp(0);            // click: pick up logs
            iv.SlotDown(20, 0, false); iv.SlotUp(20);          // click: place in the pack
            Check("move_stack", inv.Slots[0].IsEmpty && inv.Slots[20].Id == B.LogOak && inv.Slots[20].Count == 3 && iv.CursorStack.IsEmpty);
            iv.SlotDown(1, 0, false); iv.SlotUp(30);           // drag-and-drop stone 1 -> 30
            Check("drag_and_drop", inv.Slots[1].IsEmpty && inv.Slots[30].Id == B.Stone && inv.Slots[30].Count == 10);
            iv.SlotDown(30, 1, false);                          // right-click: take half
            iv.SlotDown(31, 0, false); iv.SlotUp(31);
            Check("split_stack", inv.Slots[30].Count == 5 && inv.Slots[31].Count == 5, $"{inv.Slots[30].Count}/{inv.Slots[31].Count}");
            iv.SlotDown(31, 0, true);                           // shift-click to the hotbar
            Check("quick_move", inv.Slots[31].IsEmpty && inv.Slots[0].Id == B.Stone && inv.Slots[0].Count == 5, $"slot0={ItemDb.NameOf(inv.Slots[0].Id)}x{inv.Slots[0].Count}");
            iv.SlotDown(0, 1, false); iv.SlotDown(1, 1, false); // right-click place one
            Check("place_one", iv.CursorStack.Count == 2 && inv.Slots[1].Id == B.Stone && inv.Slots[1].Count == 1, $"cursor={iv.CursorStack.Count}");
            iv.SlotDown(0, 0, false);                           // put the rest back
            Check("merge_back", iv.CursorStack.IsEmpty && inv.Slots[0].Count == 4, $"slot0={inv.Slots[0].Count}");

            // Crafting.
            var planks = Crafting.Recipes.Find(r => r.Key == "planks_" + B.LogOak);
            iv.Select(planks);
            int one = iv.CraftSelected(false);
            Check("craft_planks", one == 1 && inv.Count(B.Planks) == 4 && inv.Count(B.LogOak) == 2, $"planks={inv.Count(B.Planks)} logs={inv.Count(B.LogOak)}");
            int max = iv.CraftSelected(true);
            Check("craft_max", max == 2 && inv.Count(B.Planks) == 12 && inv.Count(B.LogOak) == 0, $"made={max} planks={inv.Count(B.Planks)}");
            var pick = Crafting.Recipes.Find(r => r.Key == "pickaxe_wood");
            iv.Select(pick);
            int noBench = iv.CraftSelected(false);
            Check("station_required", noBench == 0 || iv.NearbyStations.Contains(Station.Workbench), $"near={string.Join(",", iv.NearbyStations)}");

            // Drop outside the window.
            int drops = ItemDrop.All.Count;
            int before = inv.Count(B.Planks);
            int slot = -1;
            for (int i = 0; i < Inventory.Size; i++) if (inv.Slots[i].Id == B.Planks) { slot = i; break; }
            iv.SlotDown(slot, 0, false);   // pick up the stack
            iv.DropCursor(true);           // click outside with the right button: drop one
            bool oneDropped = iv.CursorStack.Count == before - 1;
            iv.DropCursor(false);          // left: drop the rest...
            yield return null;
            Check("drop_outside", oneDropped && ItemDrop.All.Count == drops + 2 && inv.Count(B.Planks) == 0 && iv.CursorStack.IsEmpty, $"drops {drops}->{ItemDrop.All.Count}");
            inv.Add(B.Planks, 11);         // ...and take them back for the station test

            // Esc closes the inventory without pausing.
            UI.Back();
            yield return null; yield return null;
            Check("esc_closes_inventory", UI.Top == null && Game.State == GameState.Playing && !Game.UiCapture, $"state={Game.State} capture={Game.UiCapture}");

            // Workbench right-click path.
            UI.OpenInventory(true, Station.Workbench);
            yield return Wait(0.2f);
            Check("open_from_station", UI.Top == UI.Inventory && iv.NearbyStations.Contains(Station.Workbench) && iv.Selected != null && iv.Selected.Station == Station.Workbench,
                $"selected={iv.Selected?.Key}");
            iv.Select(Crafting.Recipes.Find(r => r.Key == "stick"));
            int sticks = iv.CraftSelected(false);
            iv.Select(pick);
            Check("craft_at_station", sticks == 1 && iv.CraftSelected(false) == 1 && inv.Count(ItemIds.PickaxeWood) == 1,
                $"pick={inv.Count(ItemIds.PickaxeWood)} sticks={inv.Count(ItemIds.Stick)} planks={inv.Count(B.Planks)}");
            UI.Back();
            yield return Wait(0.2f);

            // Pause, settings inside pause, back, resume.
            Game.Pause();
            yield return null;
            Check("pause_menu", Game.State == GameState.Paused && UI.Top == UI.Pause);
            yield return Wait(0.3f);
            CheckLayout("pause", UI.Pause.Root, "side-column", "menu-item");
            UI.Push(UI.Settings);
            yield return Wait(0.2f);
            UI.Back();
            yield return Wait(0.2f);
            Check("pause_settings_back", Game.State == GameState.Paused && UI.Top == UI.Pause, $"top={UI.Top?.Id}");
            UI.Pause.Resume();
            yield return Wait(0.2f);
            Check("resume", Game.State == GameState.Playing && UI.Top == null && !Game.UiCapture);

            // Pickup toast.
            int toasts = UI.Hud.ToastCount;
            ItemDrop.Spawn(ItemStack.Of(B.Cobblestone, 2), p.transform.position + Vector3.up * 0.9f, Vector3.zero, 0f);
            yield return WaitFor(() => UI.Hud.ToastCount > toasts, 3f);
            Check("pickup_toast", UI.Hud.ToastCount > toasts, $"last='{UI.Hud.LastToast}'");

            // Death and respawn.
            p.Hurt(100f, "fall");
            yield return Wait(0.3f);
            Check("death_screen", UI.Top == UI.Death && Game.UiCapture);
            CheckLayout("death", UI.Death.Root, "death-center", "btn");
            UI.Death.Respawn();
            yield return Wait(0.3f);
            Check("respawn", !p.Dead && UI.Top == null && !Game.UiCapture);

            Check("ui_sounds_requested", UiSound.Count > 10, $"count={UiSound.Count}");

            // Back to the title.
            Game.QuitToMenu();
            yield return Wait(0.5f);
            Check("quit_to_title", Game.State == GameState.MainMenu && UI.Top == UI.MainMenu && !UI.Hud.Visible);
            Finish();
        }

        void Finish()
        {
            RestoreSettings();
            CleanupTempWorlds();
            Debug.Log($"WM_UITEST {(mFail == 0 ? "PASS" : "FAIL")} summary pass={mPass} fail={mFail}");
            Quit(mFail == 0 ? 0 : 1);
        }

        static void Quit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(code);
#endif
        }
    }
}
