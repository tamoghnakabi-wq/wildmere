using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.UI
{
    /// <summary>Title screen: a translucent overlay over the live world backdrop.</summary>
    public sealed class MainMenuScreen : UiScreen
    {
        public override string Id => "mainmenu";
        VisualElement mMenu;
        Label mVersion;

        public override void Build()
        {
            var shade = Root.Append(Ui.Div("mm-shade").Ignore());
            shade.style.backgroundImage = UiTextures.MenuShade;
            var bottom = Root.Append(Ui.Div("mm-shade-bottom").Ignore());
            bottom.style.backgroundImage = UiTextures.BottomShade;

            var col = Root.Append(Ui.Div("mm-column", "anim-left"));
            var mark = col.Append(Ui.Div("wordmark"));
            mark.Add(new Glyph(GlyphKind.Emblem, "wordmark__emblem"));
            var words = mark.Append(Ui.Div());
            words.Add(Ui.Text("WILDMERE", "t-display", "wordmark__text", "t-shadow"));
            words.Add(Ui.Text("SHAPE THE WILD", "wordmark__tag"));
            mMenu = col.Append(Ui.Div("menu", "mm-menu"));

            var foot = Root.Append(Ui.Div("mm-foot", "anim-rise"));
            mVersion = foot.Append(Ui.Text("", "mm-version"));
            foot.Add(Ui.Div("grow"));
            foot.Add(Ui.Text("Arrow keys to navigate  ·  Enter to select", "mm-version"));
        }

        protected override void OnShow()
        {
            mVersion.text = $"Wildmere {GameRoot.Version}";
            mMenu.Clear();
            var worlds = WorldSave.List();
            Button first;
            if (worlds.Count > 0)
            {
                var w = worlds[0];
                first = mMenu.Append(Ui.MenuItem("Continue", () => UI.PlayWorld(w), $"{w.Name}  ·  {LastPlayed(w)}", UiSound.Confirm));
                first.AddToClassList("menu-item--primary");
                mMenu.Add(Ui.MenuItem("New World", () => UI.Push(UI.NewWorld)));
            }
            else
            {
                first = mMenu.Append(Ui.MenuItem("New World", () => UI.Push(UI.NewWorld)));
                first.AddToClassList("menu-item--primary");
            }
            mMenu.Add(Ui.MenuItem("Load World", () => UI.Push(UI.LoadWorld), worlds.Count > 0 ? $"{worlds.Count} {Ui.Plural(worlds.Count, "world", "worlds")}" : null));
            mMenu.Add(Ui.MenuItem("Settings", () => UI.Push(UI.Settings)));
            var quit = mMenu.Append(Ui.MenuItem("Quit", Application.Quit));
            quit.AddToClassList("menu-item--quiet");
            DefaultFocus = first;
        }

        public static string LastPlayed(WorldSave w)
        {
            var s = Json.Str(w.Meta, "lastPlayed", null) ?? Json.Str(w.Meta, "created", null);
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                return Ui.Ago(t.ToUniversalTime());
            return "never played";
        }

        public override bool OnBack() => true; // nothing below the title screen
        public override bool CapturesMouse => false;
    }

    /// <summary>Create-a-world form. World options travel to GameRoot.NewWorld as a settings dictionary.</summary>
    public sealed class NewWorldScreen : UiScreen
    {
        public override string Id => "newworld";
        public TextField NameField { get; private set; }
        public TextField SeedField { get; private set; }
        public WmSwitch StarterKit { get; private set; }
        public WmChoice DayLength { get; private set; }
        Label mDayHint;
        static readonly int[] kDayMinutes = { 10, 20, 40 };

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "scrim"));
            var center = Root.Append(Ui.Div("fill", "center"));
            var wrap = center.Append(Ui.Panel(out var panel));
            wrap.AddToClassList("anim-pop");
            panel.style.width = 700;

            var head = panel.Append(Ui.Div("panel__header"));
            head.Add(Ui.Text("Create a world", "panel__title"));
            head.Add(Ui.Text("Every seed grows a different wild.", "panel__subtitle"));

            var nameRow = panel.Append(Ui.Div("form-row"));
            nameRow.Add(Ui.Text("WORLD NAME", "form-label"));
            NameField = nameRow.Append(new TextField { maxLength = 40 }.Cls("wm-input"));
            NameField.textEdition.placeholder = "New World";

            var seedRow = panel.Append(Ui.Div("form-row"));
            seedRow.Add(Ui.Text("SEED", "form-label"));
            var seedLine = seedRow.Append(Ui.Div("row"));
            SeedField = seedLine.Append(new TextField { maxLength = 32 }.Cls("wm-input", "gap-r-12"));
            SeedField.textEdition.placeholder = "Random";
            var dice = seedLine.Append(Ui.Btn("", Randomise, null, "btn--square"));
            dice.tooltip = "Random seed";
            dice.Add(new Glyph(GlyphKind.Dice, "btn__glyph"));
            seedRow.Add(Ui.Text("Leave blank for a surprise. Numbers and words both work.", "form-hint"));

            var kitRow = panel.Append(Ui.Div("row", "form-row"));
            var kitText = kitRow.Append(Ui.Div("grow"));
            kitText.Add(Ui.Text("STARTER KIT", "form-label"));
            kitText.Add(Ui.Text("Begin with a few torches to see you through the first night.", "form-hint"));
            StarterKit = kitRow.Append(new WmSwitch());

            var dayRow = panel.Append(Ui.Div("form-row"));
            dayRow.Add(Ui.Text("DAY LENGTH", "form-label"));
            DayLength = dayRow.Append(new WmChoice(new[] { "SHORT", "NORMAL", "LONG" }));
            DayLength.Changed += _ => SyncDayHint();
            mDayHint = dayRow.Append(Ui.Text("", "form-hint"));

            var foot = panel.Append(Ui.Div("panel__footer"));
            foot.Add(Ui.Btn("Back", () => UI.Pop(this), UiSound.Close, "btn--ghost"));
            foot.Add(Ui.Div("grow"));
            var create = foot.Append(Ui.Btn("Create World", Create, UiSound.Confirm, "btn--primary"));
            create.style.minWidth = 220;

            panel.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Create(); e.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            DefaultFocus = NameField;
        }

        protected override void OnShow()
        {
            NameField.value = SuggestName();
            SeedField.value = "";
            StarterKit.Value = true;
            DayLength.Index = 1;
            SyncDayHint();
        }

        static string SuggestName()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in WorldSave.List()) names.Add(w.Name);
            if (!names.Contains("New World")) return "New World";
            for (int i = 2; ; i++) if (!names.Contains($"New World {i}")) return $"New World {i}";
        }

        void SyncDayHint()
        {
            int i = Mathf.Clamp(DayLength.Index, 0, 2);
            mDayHint.text = $"A full day and night takes {kDayMinutes[i]} minutes.";
        }

        void Randomise()
        {
            SeedField.value = UnityEngine.Random.Range(1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Keys passed in GameRoot.NewWorld's settings dictionary (see the UI report).</summary>
        public Dictionary<string, object> WorldSettings()
        {
            int i = Mathf.Clamp(DayLength.Index, 0, 2);
            // Contract with GameRoot.NewWorld: "starterKit" (bool) and "dayLengthMinutes" (double).
            return new Dictionary<string, object>
            {
                { "starterKit", StarterKit.Value },
                { "dayLengthMinutes", (double)kDayMinutes[i] },
            };
        }

        public void Create()
        {
            if (!IsOpen || UI.Game == null) return;
            string name = string.IsNullOrWhiteSpace(NameField.value) ? "New World" : NameField.value.Trim();
            long seed = GameRoot.SeedFromText(SeedField.value);
            Debug.Log($"WM_UI new world name='{name}' seed={seed}");
            UI.Game.NewWorld(name, seed, WorldSettings());
        }
    }

    /// <summary>Saved worlds: pick, play, or delete (with confirmation).</summary>
    public sealed class LoadWorldScreen : UiScreen
    {
        public override string Id => "load";
        VisualElement mList, mEmpty;
        ScrollView mScroll;
        Label mCount;
        Button mPlay, mDelete;
        readonly List<(WorldSave w, VisualElement card)> mCards = new List<(WorldSave, VisualElement)>();
        public WorldSave Selected { get; private set; }
        public int WorldCount => mCards.Count;

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "scrim"));
            var center = Root.Append(Ui.Div("fill", "center"));
            var wrap = center.Append(Ui.Panel(out var panel, "worlds-panel"));
            wrap.AddToClassList("anim-pop");

            var head = panel.Append(Ui.Div("panel__header"));
            head.Add(Ui.Text("Your worlds", "panel__title"));
            mCount = head.Append(Ui.Text("", "panel__subtitle"));

            var body = panel.Append(Ui.Div("grow"));
            mScroll = Ui.Scroll(body);
            mList = mScroll.contentContainer;
            mEmpty = body.Append(Ui.Div("empty", "hidden"));
            mEmpty.Add(new Glyph(GlyphKind.Emblem, "empty__glyph"));
            mEmpty.Add(Ui.Text("No worlds yet", "t-h2", "gap-b-8"));
            mEmpty.Add(Ui.Text("Create one and start exploring.", "t-dim", "gap-b-24"));
            mEmpty.Add(Ui.Btn("Create a world", () => { UI.Pop(this); UI.Push(UI.NewWorld); }, null, "btn--primary"));

            var foot = panel.Append(Ui.Div("panel__footer"));
            mDelete = foot.Append(Ui.IconBtn(GlyphKind.Trash, "Delete", AskDelete, null, "btn--danger"));
            foot.Add(Ui.Div("grow"));
            foot.Add(Ui.Btn("Back", () => UI.Pop(this), UiSound.Close, "btn--ghost", "gap-r-12"));
            mPlay = foot.Append(Ui.IconBtn(GlyphKind.Play, "Play", () => UI.PlayWorld(Selected), UiSound.Confirm, "btn--primary"));
            mPlay.style.minWidth = 170;
        }

        protected override void OnShow() => Refresh();

        public void Refresh()
        {
            mList.Clear();
            mCards.Clear();
            var worlds = WorldSave.List();
            mCount.text = worlds.Count == 0 ? "Nothing saved yet" : $"{worlds.Count} {Ui.Plural(worlds.Count, "world", "worlds")}  ·  most recent first";
            mScroll.style.display = worlds.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            mEmpty.EnableInClassList("hidden", worlds.Count > 0);
            foreach (var w in worlds) mCards.Add((w, mList.Append(Card(w))));
            Select(worlds.Count > 0 ? worlds[0] : null);
            DefaultFocus = mCards.Count > 0 ? mCards[0].card : null;
        }

        static readonly Color[] kTileColors =
        {
            new Color32(47, 127, 115, 255), new Color32(90, 122, 58, 255), new Color32(154, 116, 50, 255), new Color32(154, 85, 67, 255),
            new Color32(110, 79, 134, 255), new Color32(70, 96, 127, 255), new Color32(47, 94, 74, 255), new Color32(122, 90, 112, 255),
        };

        VisualElement Card(WorldSave w)
        {
            var card = Ui.Div("world-card");
            card.focusable = true;
            var tile = card.Append(Ui.Div("world-card__tile").Ignore());
            uint h = (uint)(w.Seed ^ (w.Seed >> 32)) * 2654435761u;
            tile.style.backgroundColor = kTileColors[(h >> 24) % (uint)kTileColors.Length];
            tile.Add(Ui.Text(w.Name.Length > 0 ? w.Name.Substring(0, 1).ToUpperInvariant() : "W", "world-card__initial").Ignore());
            var text = card.Append(Ui.Div("grow").Ignore());
            text.Add(Ui.Text(w.Name, "world-card__name").Ignore());
            double play = Json.Num(w.Meta, "playTime");
            text.Add(Ui.Text($"Last played {MainMenuScreen.LastPlayed(w)}  ·  {Ui.Duration(play)} played  ·  Seed {w.Seed}", "world-card__meta").Ignore());
            var go = card.Append(new Glyph(GlyphKind.Play, "btn__glyph", "world-card__play", "t-accent"));
            go.pickingMode = PickingMode.Ignore;

            card.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                if (e.clickCount >= 2 && Selected == w) { UI.PlayWorld(w); return; }
                UiSound.Play(UiSound.Click);
                Select(w);
            });
            card.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
            card.RegisterCallback<FocusInEvent>(_ => Select(w));
            card.RegisterCallback<NavigationSubmitEvent>(_ => UI.PlayWorld(w));
            return card;
        }

        public void Select(WorldSave w)
        {
            Selected = w;
            foreach (var (cw, card) in mCards) card.EnableInClassList("world-card--on", cw == w);
            mPlay.SetEnabled(w != null);
            mDelete.SetEnabled(w != null);
        }

        public void AskDelete()
        {
            var w = Selected;
            if (w == null) return;
            UI.Confirm($"Delete “{w.Name}”?", "This world and everything built in it will be gone for good. This can't be undone.",
                "Delete world", true, () => Delete(w));
        }

        public void Delete(WorldSave w)
        {
            try { w.Delete(); Debug.Log($"WM_UI deleted world '{w.Name}'"); }
            catch (Exception e) { Debug.LogWarning($"WM_UI delete failed: {e.Message}"); UiSound.Play(UiSound.Error); }
            Refresh();
        }
    }

    /// <summary>Generic modal confirmation.</summary>
    public sealed class ConfirmDialog : UiScreen
    {
        public override string Id => "confirm";
        Label mTitle, mMsg;
        Button mYes, mNo;
        Action mOnYes;

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "scrim--heavy"));
            var center = Root.Append(Ui.Div("fill", "center"));
            var wrap = center.Append(Ui.Panel(out var panel, "dialog"));
            wrap.AddToClassList("anim-pop");
            mTitle = panel.Append(Ui.Text("", "t-h2", "t-wrap"));
            mMsg = panel.Append(Ui.Text("", "dialog__msg"));
            var foot = panel.Append(Ui.Div("panel__footer"));
            foot.Add(Ui.Div("grow"));
            mNo = foot.Append(Ui.Btn("Cancel", () => UI.Pop(this), UiSound.Close, "btn--ghost", "gap-r-12"));
            mYes = foot.Append(Ui.Btn("Confirm", Yes, UiSound.Confirm, "btn--primary"));
            DefaultFocus = mNo;
        }

        public void Setup(string title, string message, string yes, bool danger, Action onYes)
        {
            mTitle.text = title;
            mMsg.text = message;
            mYes.text = yes;
            mYes.EnableInClassList("btn--danger", danger);
            mYes.EnableInClassList("btn--primary", !danger);
            mOnYes = onYes;
        }

        public void Yes()
        {
            var a = mOnYes;
            mOnYes = null;
            UI.Pop(this);
            a?.Invoke();
        }

        public override bool CapturesMouse => true;
    }

    /// <summary>Pause menu over the frozen world.</summary>
    public sealed class PauseScreen : UiScreen
    {
        public override string Id => "pause";
        Label mSub;
        Button mResume;

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "scrim"));
            var shade = Root.Append(Ui.Div("mm-shade").Ignore());
            shade.style.backgroundImage = UiTextures.MenuShade;
            var col = Root.Append(Ui.Div("side-column", "anim-left"));
            col.Add(Ui.Text("Paused", "pause-title", "t-shadow"));
            mSub = col.Append(Ui.Text("", "pause-sub"));
            var menu = col.Append(Ui.Div("menu"));
            mResume = menu.Append(Ui.MenuItem("Resume", Resume, null, UiSound.Close));
            mResume.AddToClassList("menu-item--primary");
            menu.Add(Ui.MenuItem("Settings", () => UI.Push(UI.Settings)));
            menu.Add(Ui.MenuItem("Save World", Save));
            menu.Add(Ui.MenuItem("Save & Quit to Title", () => UI.Game?.QuitToMenu(), null, UiSound.Confirm));
            DefaultFocus = mResume;
        }

        protected override void OnShow()
        {
            var g = UI.Game;
            string name = g?.Save != null ? g.Save.Name : "";
            mSub.text = g == null ? "" : $"{name}  ·  {Ui.Duration(g.PlayTime)} played";
        }

        public void Resume() => UI.Game?.Resume();

        void Save()
        {
            var g = UI.Game;
            if (g == null) return;
            if (g.NoSave) { UI.Hud.Toast(0, 0, "Saving is off for this session", warn: true); UiSound.Play(UiSound.Error); return; }
            g.SaveWorld();
        }

        public override bool OnBack() { Resume(); return true; }
        public override bool CapturesMouse => false;
    }

    /// <summary>World generation progress with rotating tips.</summary>
    public sealed class LoadingScreen : UiScreen
    {
        public override string Id => "loading";
        VisualElement mFill, mTip;
        Label mStatus, mPct, mTipText;
        float mTipTimer;
        int mTipIndex;

        static readonly string[] kTips =
        {
            "Right-click a placed workbench to craft with its recipes unlocked.",
            "Hold Shift when you press Craft to make as many as you can.",
            "Shift-click an item to move it between your hotbar and your pack.",
            "Right-click a stack in your inventory to pick up half of it.",
            "Torches push back the dark. Keep a few for your first night.",
            "Iron ore needs a stone pickaxe or better.",
            "Gravel sometimes gives up a shard of flint.",
            "Sand and coal in a furnace make glass.",
            "Press F1 to hide the interface and take in the view.",
            "Your world saves itself every couple of minutes.",
            "A long fall hurts. Water breaks your fall.",
            "Air runs short underwater. Watch the bubbles above your hotbar.",
        };

        public override void Build()
        {
            var c = Root.Append(Ui.Div("load-center", "anim-rise"));
            c.Add(new Glyph(GlyphKind.Emblem, "load-emblem"));
            c.Add(Ui.Text("WILDMERE", "load-title", "t-shadow"));
            mStatus = c.Append(Ui.Text("Shaping the land", "load-status"));
            var bar = c.Append(Ui.Div("progress"));
            mFill = bar.Append(Ui.Div("progress__fill"));
            mPct = c.Append(Ui.Text("0%", "load-pct"));
            mTip = Root.Append(Ui.Div("load-tip"));
            mTip.Add(Ui.Text("TIP", "t-micro", "load-tip__label"));
            mTipText = mTip.Append(Ui.Text("", "load-tip__text"));
        }

        protected override void OnShow()
        {
            mTipIndex = UnityEngine.Random.Range(0, kTips.Length);
            mTipText.text = kTips[mTipIndex];
            mTip.RemoveFromClassList("load-tip--out");
            mTipTimer = 0;
            mFill.style.width = Length.Percent(0);
        }

        public override void Tick(float dt)
        {
            float p = UI.Game != null ? Mathf.Clamp01(UI.Game.LoadProgress) : 0f;
            mFill.style.width = Length.Percent(p * 100f);
            mPct.text = $"{Mathf.RoundToInt(p * 100f)}%";
            mStatus.text = p < 0.35f ? "Shaping the land" : p < 0.75f ? "Growing the wild" : p < 0.99f ? "Settling in" : "Ready";
            mTipTimer += dt;
            if (mTipTimer > 4.6f && !mTip.ClassListContains("load-tip--out")) mTip.AddToClassList("load-tip--out");
            if (mTipTimer > 5.1f)
            {
                mTipTimer = 0;
                mTipIndex = (mTipIndex + 1) % kTips.Length;
                mTipText.text = kTips[mTipIndex];
                mTip.RemoveFromClassList("load-tip--out");
            }
        }

        public override bool OnBack() => true;
        public override bool CapturesMouse => false;
    }

    /// <summary>Shown when the player dies: respawn or leave.</summary>
    public sealed class DeathScreen : UiScreen
    {
        public override string Id => "death";
        public string LastCause;
        Label mSub;

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "death-scrim"));
            var vig = Root.Append(Ui.Div("bg-vignette", "death-vignette").Ignore());
            vig.style.backgroundImage = UiTextures.LowHealth;
            var c = Root.Append(Ui.Div("death-center", "anim-rise"));
            c.Add(Ui.Text("You perished", "death-title"));
            mSub = c.Append(Ui.Text("", "death-sub"));
            var row = c.Append(Ui.Div("row"));
            var respawn = row.Append(Ui.Btn("Respawn", Respawn, UiSound.Confirm, "btn--primary", "gap-r-12"));
            respawn.style.minWidth = 200;
            row.Add(Ui.Btn("Save & Quit to Title", () => UI.Game?.QuitToMenu(), null, "btn--ghost"));
            DefaultFocus = respawn;
        }

        protected override void OnShow()
        {
            mSub.text = LastCause switch
            {
                "fall" => "You fell from a great height.",
                "drown" => "You ran out of air.",
                "void" => "You fell out of the world.",
                _ => "The wild claimed you this time.",
            };
        }

        public void Respawn()
        {
            var p = UI.Game?.Player;
            if (p == null) return;
            p.Respawn();
            UI.Pop(this);
        }

        public override bool OnBack() => true;
    }
}
