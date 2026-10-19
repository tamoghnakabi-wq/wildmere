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
    /// In-game heads-up display: hotbar (animated selector, counts, durability), segmented health with
    /// a lagging damage ghost, air bubbles, cross-hair with mining ring, target chip, pickup toasts,
    /// damage / low-health vignettes and the "Saved" indicator. Updates only what changed.
    /// </summary>
    public sealed class Hud
    {
        public readonly VisualElement Root;
        public const int SlotPitch = 70;

        readonly VisualElement mVignette, mLowVignette, mHotbar, mSelector, mToasts, mSaved, mTarget, mTargetIcon, mHealth, mAir;
        readonly Label mItemName, mTargetName, mTargetNote;
        readonly Crosshair mCross;
        readonly SlotView[] mSlots = new SlotView[Inventory.Hotbar];
        readonly VisualElement[] mHpFill = new VisualElement[10], mHpGhost = new VisualElement[10], mBubbles = new VisualElement[10];
        readonly List<ToastItem> mToastList = new List<ToastItem>();
        readonly Dictionary<int, int> mSnapshot = new Dictionary<int, int>();

        PlayerController mPlayer;
        Inventory mInv;
        bool mVisible, mUserHidden;
        float mFlash, mItemNameTimer, mSavedTimer, mPulse;
        int mLastSel = -1, mLastSelId = -1, mLastAirShown = -1, mLastModeShown = -1;
        Label mMode;
        public string ModeText => mMode != null && mMode.style.display.value != DisplayStyle.None ? mMode.text : "";
        public bool HealthShown => mHealth != null && mHealth.style.display.value != DisplayStyle.None;
        float mLastHealth = -1;
        byte mLastTargetId;
        bool mLastTargetOn;

        public bool Visible => mVisible && !mUserHidden;
        public int ToastCount => mToastList.Count;
        public string LastToast { get; private set; }

        sealed class ToastItem
        {
            public VisualElement El;
            public Label Count, Name;
            public int Id, Amount;
            public float Life;
            public bool Leaving;
        }

        public Hud()
        {
            Root = Ui.Div("hud").Ignore();
            Root.style.display = DisplayStyle.None;

            mLowVignette = Root.Append(Ui.Div("hud-vignette").Ignore());
            mLowVignette.style.backgroundImage = UiTextures.LowHealth;
            mVignette = Root.Append(Ui.Div("hud-vignette").Ignore());
            mVignette.style.backgroundImage = UiTextures.Damage;

            mCross = Root.Append(new Crosshair());

            mTarget = Root.Append(Ui.Div("hud-target").Ignore());
            var pill = mTarget.Append(Ui.Div("hud-target__pill").Ignore());
            mTargetIcon = pill.Append(Ui.Div("hud-target__icon").Ignore());
            mTargetName = pill.Append(Ui.Text("", "hud-target__name").Ignore());
            mTargetNote = pill.Append(Ui.Text("", "hud-target__note").Ignore());

            mToasts = Root.Append(Ui.Div("toasts").Ignore());

            mSaved = Root.Append(Ui.Div("hud-saved").Ignore());
            mSaved.Add(new Glyph(GlyphKind.Check, "hud-saved__glyph", "t-accent"));
            mSaved.Add(Ui.Text("World saved", "hud-saved__text").Ignore());

            var bottom = Root.Append(Ui.Div("hud-bottom").Ignore());
            mItemName = bottom.Append(Ui.Text("", "hud-itemname").Ignore());
            var status = bottom.Append(Ui.Div("hud-status").Ignore());
            mMode = status.Append(Ui.Text("", "hud-mode").Ignore());
            mHealth = status.Append(Ui.Div("health").Ignore());
            mHealth.Add(new Glyph(GlyphKind.Heart, "health__glyph"));
            for (int i = 0; i < 10; i++)
            {
                var seg = mHealth.Append(Ui.Div("hp-seg").Ignore());
                mHpGhost[i] = seg.Append(Ui.Div("hp-seg__ghost").Ignore());
                mHpFill[i] = seg.Append(Ui.Div("hp-seg__fill").Ignore());
            }
            mAir = status.Append(Ui.Div("air").Ignore());
            for (int i = 0; i < 10; i++) mBubbles[i] = mAir.Append(Ui.Div("bubble").Ignore());

            mHotbar = bottom.Append(Ui.Div("hotbar").Ignore());
            for (int i = 0; i < Inventory.Hotbar; i++)
            {
                mSlots[i] = new SlotView(i + 1);
                mSlots[i].Root.pickingMode = PickingMode.Ignore;
                mHotbar.Add(mSlots[i].Root);
            }
            mSelector = mHotbar.Append(Ui.Div("hotbar__sel").Ignore());
        }

        // ------------------------------------------------------------------ binding

        public void Bind(PlayerController p)
        {
            if (mInv != null) mInv.Changed -= OnInventoryChanged;
            mPlayer = p;
            mInv = p != null ? p.Inventory : null;
            if (mInv != null) mInv.Changed += OnInventoryChanged;
            mLastSel = -1; mLastSelId = -1; mLastHealth = -1; mLastAirShown = -1; mLastModeShown = -1;
            foreach (var t in mToastList) t.El.RemoveFromHierarchy();
            mToastList.Clear();
            mFlash = 0;
            Resnapshot();
            OnInventoryChanged();
        }

        public void SetVisible(bool v)
        {
            mVisible = v;
            Root.style.display = v ? DisplayStyle.Flex : DisplayStyle.None;
            if (!v) { mUserHidden = false; Root.RemoveFromClassList("hud--off"); }
        }

        public void ToggleHidden()
        {
            mUserHidden = !mUserHidden;
            Root.EnableInClassList("hud--off", mUserHidden);
        }

        void OnInventoryChanged()
        {
            if (mInv == null) return;
            for (int i = 0; i < Inventory.Hotbar; i++) mSlots[i].Set(mInv.Slots[i]);
        }

        // ------------------------------------------------------------------ events

        public void Damage(float amount)
        {
            mFlash = Mathf.Min(1f, Mathf.Max(mFlash, 0.4f + amount / 8f));
        }

        public void ShowSaved()
        {
            mSavedTimer = 2.6f;
            mSaved.AddToClassList("hud-saved--on");
        }

        /// <summary>An item drop was collected: toast the amount gained since the last frame.</summary>
        public void NotePickup(int id)
        {
            if (mInv == null || id == 0) return;
            int now = mInv.Count(id);
            mSnapshot.TryGetValue(id, out int before);
            mSnapshot[id] = now;
            if (now > before) Toast(id, now - before, null);
        }

        void Resnapshot()
        {
            mSnapshot.Clear();
            if (mInv == null) return;
            foreach (var s in mInv.Slots)
                if (!s.IsEmpty) { mSnapshot.TryGetValue(s.Id, out int n); mSnapshot[s.Id] = n + s.Count; }
        }

        public void Toast(int id, int amount, string text, bool warn = false)
        {
            LastToast = text ?? $"+{amount} {ItemDb.NameOf(id)}";
            if (amount > 0)
                foreach (var t in mToastList)
                    if (!t.Leaving && t.Id == id && t.Amount > 0)
                    {
                        t.Amount += amount;
                        t.Count.text = $"+{t.Amount}";
                        t.Life = 3.2f;
                        return;
                    }
            if (mToastList.Count >= 5) Dismiss(mToastList[0]);
            var el = Ui.Div("toast", "toast--in").Ignore();
            if (warn) el.AddToClassList("toast--warn");
            var icon = el.Append(Ui.Div("toast__icon").Ignore());
            if (id != 0) ItemIcons.Set(icon, id); else icon.style.display = DisplayStyle.None;
            var count = el.Append(Ui.Text(amount > 0 ? $"+{amount}" : "", "toast__count").Ignore());
            if (amount <= 0) count.style.display = DisplayStyle.None;
            var name = el.Append(Ui.Text(text ?? ItemDb.NameOf(id), "toast__name").Ignore());
            if (warn && amount <= 0) name.AddToClassList("t-warn");
            mToasts.Add(el);
            el.schedule.Execute(() => el.RemoveFromClassList("toast--in")).ExecuteLater(16);
            mToastList.Add(new ToastItem { El = el, Count = count, Name = name, Id = id, Amount = amount, Life = 3.2f });
        }

        void Dismiss(ToastItem t)
        {
            if (t.Leaving) return;
            t.Leaving = true;
            t.Life = 0.3f;
            t.El.AddToClassList("toast--out");
        }

        // ------------------------------------------------------------------ frame

        public void Tick(float dt, GameRoot game)
        {
            if (!mVisible || mPlayer == null || mInv == null) return;
            var ui = UiRoot.Instance;
            bool overlay = ui != null && ui.Stack.Count > 0;
            // The inventory/death screens own the bottom of the screen; the pause menu keeps the HUD.
            Root.EnableInClassList("hud--overlay", overlay && !(ui.Top is PauseScreen) && ui.Stack[0] != ui.Pause);

            // Hotbar selection + item name.
            int sel = mInv.Selected;
            int selId = mInv.Slots[sel].Id;
            bool selChanged = sel != mLastSel;
            if (selChanged)
            {
                mSelector.style.translate = new Translate(sel * SlotPitch, 0);
                mLastSel = sel;
            }
            if (selId != mLastSelId || selChanged)
            {
                mLastSelId = selId;
                if (selId != 0) { mItemName.text = ItemDb.NameOf(selId); mItemNameTimer = 2.2f; mItemName.AddToClassList("hud-itemname--on"); }
                else { mItemNameTimer = 0; mItemName.RemoveFromClassList("hud-itemname--on"); }
            }
            if (mItemNameTimer > 0) { mItemNameTimer -= dt; if (mItemNameTimer <= 0) mItemName.RemoveFromClassList("hud-itemname--on"); }

            // Creative: a mode label instead of health and air (nothing can hurt or drown the player).
            int modeShown = !mPlayer.Creative ? 0 : mPlayer.Flying ? 2 : 1;
            if (modeShown != mLastModeShown)
            {
                mLastModeShown = modeShown;
                mMode.text = modeShown == 2 ? "CREATIVE  ·  FLYING" : modeShown == 1 ? "CREATIVE" : "";
                mMode.style.display = modeShown > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                mHealth.style.display = modeShown > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            }

            // Health.
            float hp = mPlayer.Health;
            if (Mathf.Abs(hp - mLastHealth) > 0.01f)
            {
                for (int i = 0; i < 10; i++)
                {
                    float f = Mathf.Clamp01((hp - i * 2f) / 2f);
                    mHpFill[i].style.width = Length.Percent(f * 100f);
                    // The ghost trails damage (USS transition with delay) and snaps up on heals.
                    mHpGhost[i].style.width = Length.Percent(f * 100f);
                }
                mHealth.EnableInClassList("health--low", hp <= 6f && hp > 0f);
                mLastHealth = hp;
            }
            bool low = hp <= 6f && hp > 0f;
            mPulse += dt;
            mHealth.EnableInClassList("health--pulse", low && Mathf.Repeat(mPulse, 1.1f) < 0.18f);

            // Air.
            bool showAir = !mPlayer.Creative && (mPlayer.HeadUnderwater || mPlayer.Air < PlayerController.MaxAir - 0.01f);
            mAir.EnableInClassList("air--on", showAir);
            int bubbles = Mathf.CeilToInt(Mathf.Clamp(mPlayer.Air, 0f, PlayerController.MaxAir) - 0.001f);
            if (bubbles != mLastAirShown)
            {
                for (int i = 0; i < 10; i++) mBubbles[i].EnableInClassList("bubble--gone", i >= bubbles);
                mLastAirShown = bubbles;
            }

            // Cross-hair, mining ring and target chip.
            var bi = game.Interaction;
            bool hit = !overlay && !mPlayer.Dead && bi != null && bi.Target.Hit;
            mCross.style.display = overlay || mPlayer.Dead ? DisplayStyle.None : DisplayStyle.Flex;
            mCross.Set(hit, hit ? bi.MineProgress : 0f);
            byte tid = hit ? bi.Target.Id : (byte)0;
            if (hit != mLastTargetOn || tid != mLastTargetId)
            {
                mLastTargetOn = hit; mLastTargetId = tid;
                mTarget.EnableInClassList("hud-target--on", hit);
                if (hit)
                {
                    var bd = Blocks.Get(tid);
                    mTargetName.text = bd.Name;
                    ItemIcons.Set(mTargetIcon, ItemDb.Exists(tid) ? tid : 0);
                    mTargetIcon.style.display = ItemDb.Exists(tid) ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
            if (hit)
            {
                var bd = Blocks.Get(tid);
                var held = mInv.SelectedStack.Def;
                string note = "";
                if (bd.Hardness < 0) note = "Unbreakable";
                else if (!ItemDb.CanHarvest(bd, held))
                    note = bd.MinTier switch { 1 => "Needs a pickaxe", 2 => "Needs a stone pickaxe", 3 => "Needs an iron pickaxe", _ => "" };
                if (mTargetNote.text != note) { mTargetNote.text = note; mTargetNote.style.display = note.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None; }
            }

            // Vignettes.
            mFlash = Mathf.Max(0f, mFlash - dt * 1.5f);
            mVignette.style.opacity = mFlash * 0.95f;
            float lowA = low ? 0.35f + 0.22f * Mathf.Sin(mPulse * 4.2f) : 0f;
            mLowVignette.style.opacity = lowA;

            // Toasts.
            for (int i = mToastList.Count - 1; i >= 0; i--)
            {
                var t = mToastList[i];
                t.Life -= dt;
                if (t.Life <= 0)
                {
                    if (!t.Leaving) Dismiss(t);
                    else { t.El.RemoveFromHierarchy(); mToastList.RemoveAt(i); }
                }
            }

            if (mSavedTimer > 0) { mSavedTimer -= dt; if (mSavedTimer <= 0) mSaved.RemoveFromClassList("hud-saved--on"); }

            Resnapshot();
        }
    }

    /// <summary>One inventory/hotbar slot: icon, count, durability bar and optional key number.</summary>
    public sealed class SlotView
    {
        public readonly VisualElement Root, Icon, Dur, DurFill;
        public readonly Label Count;
        public int Index = -1;

        public SlotView(int keyNumber = 0)
        {
            Root = Ui.Div("slot");
            Icon = Root.Append(Ui.Div("slot__icon").Ignore());
            Count = Root.Append(Ui.Text("", "slot__count").Ignore());
            Dur = Root.Append(Ui.Div("slot__dur").Ignore());
            DurFill = Dur.Append(Ui.Div("slot__dur-fill").Ignore());
            if (keyNumber > 0) Root.Add(Ui.Text(keyNumber.ToString(), "slot__key").Ignore());
            Set(ItemStack.Empty);
        }

        public void Set(ItemStack s)
        {
            ItemIcons.Set(Icon, s.IsEmpty ? 0 : s.Id);
            Count.text = !s.IsEmpty && s.Count > 1 ? s.Count.ToString() : "";
            var d = s.IsEmpty ? null : s.Def;
            bool showDur = d != null && d.Durability > 0 && s.Durability < d.Durability;
            Dur.style.display = showDur ? DisplayStyle.Flex : DisplayStyle.None;
            if (showDur)
            {
                float f = Mathf.Clamp01(s.Durability / (float)d.Durability);
                DurFill.style.width = Length.Percent(f * 100f);
                DurFill.style.backgroundColor = DurabilityColor(f);
            }
        }

        public static Color DurabilityColor(float f) =>
            f > 0.5f ? Color.Lerp(new Color(0.95f, 0.82f, 0.35f), new Color(0.51f, 0.86f, 0.47f), (f - 0.5f) * 2f)
                     : Color.Lerp(new Color(0.94f, 0.36f, 0.33f), new Color(0.95f, 0.82f, 0.35f), f * 2f);
    }
}
