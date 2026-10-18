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
    /// Inventory (36 slots, hotbar row last) beside the crafting panel.
    ///   Left click: pick up / place / merge / swap (click-and-click or drag-and-drop)
    ///   Right click: pick up half / place one
    ///   Shift-click: quick move between hotbar and pack
    ///   Click outside the panels while holding: drop into the world (right click drops one)
    ///   Hover + 1-9: swap with that hotbar slot; hover + Q: drop one (Ctrl/Cmd: the stack)
    /// Crafting: recipes grouped by category, have/need counts, station requirement, Craft
    /// (Shift = as many as possible).
    /// </summary>
    public sealed class InventoryScreen : UiScreen
    {
        public override string Id => "inventory";

        PlayerController mPlayer;
        Inventory mInv;
        readonly SlotView[] mSlots = new SlotView[Inventory.Size];
        VisualElement mCursorEl, mCursorIcon, mTooltip, mTipDurFill, mTipDur;
        Label mCursorCount, mTipName, mTipKind, mTipDesc, mTipDurText;
        ItemStack mCursor;
        int mHover = -1, mDragFrom = -1;
        bool mDragArmed;
        Vector2 mDownPos, mPointer;

        // Crafting
        readonly List<RecipeRow> mRows = new List<RecipeRow>();
        readonly Dictionary<string, Button> mCatTabs = new Dictionary<string, Button>();
        readonly Dictionary<string, Label> mCatHeaders = new Dictionary<string, Label>();
        VisualElement mStations, mDetailIcon, mDetailInputs;
        Label mDetailName, mDetailCount, mDetailDesc, mDetailStation;
        Button mCraftBtn, mCraftAllBtn;
        ScrollView mRecipeScroll;
        string mCategory = "All";
        HashSet<Station> mNear = new HashSet<Station>();
        Station mOpenedAt = Station.None;
        float mNearTimer;
        bool mIconsReady;

        public Recipe Selected { get; private set; }
        public ItemStack CursorStack => mCursor;
        public ICollection<Station> NearbyStations => mNear;

        static readonly string[] kCategoryOrder = { "Building", "Tools", "Materials", "Lighting" };

        sealed class RecipeRow
        {
            public Recipe R;
            public VisualElement El, Icon, Inputs;
            public Label Badge;
            public readonly List<(VisualElement icon, Label text, int id, int need)> In = new List<(VisualElement, Label, int, int)>();
        }

        // ------------------------------------------------------------------ build

        public override void Build()
        {
            var scrim = Root.Append(Ui.Div("fill", "scrim"));
            scrim.RegisterCallback<PointerDownEvent>(e => { if (!mCursor.IsEmpty) { DropCursor(e.button == 1); e.StopPropagation(); } });

            var layout = Root.Append(Ui.Div("inv-layout").Ignore());

            // Inventory panel
            var invWrap = layout.Append(Ui.Panel(out var inv, "inv-panel"));
            invWrap.AddToClassList("anim-pop");
            var title = inv.Append(Ui.Div("inv-title-row"));
            title.Add(Ui.Text("Inventory", "panel__title"));
            title.Add(Ui.Text("Shift-click to move  ·  Right-click to split", "inv-hint"));
            var grid = inv.Append(Ui.Div("inv-grid"));
            for (int i = Inventory.Hotbar; i < Inventory.Size; i++) grid.Add(MakeSlot(i));
            var sep = inv.Append(Ui.Div("inv-sep"));
            sep.Add(Ui.Text("HOTBAR", "t-micro"));
            sep.Add(Ui.Div("inv-sep__line"));
            var hot = inv.Append(Ui.Div("inv-grid"));
            for (int i = 0; i < Inventory.Hotbar; i++) hot.Add(MakeSlot(i));

            // Crafting panel
            var craftWrap = layout.Append(Ui.Panel(out var craft, "craft-panel"));
            craftWrap.AddToClassList("anim-pop");
            var head = craft.Append(Ui.Div("craft-head"));
            head.Add(Ui.Text("Crafting", "panel__title"));
            mStations = head.Append(Ui.Div("craft-stations"));
            var tabs = craft.Append(Ui.Div("craft-tabs"));
            foreach (var cat in Categories())
            {
                var c = cat;
                var b = tabs.Append(new Button(() => { UiSound.Play(UiSound.Click); SetCategory(c); }) { text = c }.Cls("pill"));
                b.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
                mCatTabs[c] = b;
            }
            var listHost = craft.Append(Ui.Div("craft-list"));
            mRecipeScroll = Ui.Scroll(listHost);
            var list = mRecipeScroll.contentContainer;
            list.AddToClassList("craft-list-inner");
            foreach (var cat in Categories())
            {
                if (cat == "All") continue;
                mCatHeaders[cat] = list.Append(Ui.Text(cat.ToUpperInvariant(), "t-micro", "craft-cat"));
                foreach (var r in Crafting.Recipes)
                    if (CategoryOf(r) == cat) mRows.Add(MakeRow(list, r));
            }

            var detail = craft.Append(Ui.Div("craft-detail"));
            var dTop = detail.Append(Ui.Div("row"));
            mDetailIcon = dTop.Append(Ui.Div("craft-detail__icon").Ignore());
            var dText = dTop.Append(Ui.Div("grow"));
            var nameRow = dText.Append(Ui.Div("row"));
            mDetailName = nameRow.Append(Ui.Text("", "craft-detail__name"));
            mDetailCount = nameRow.Append(Ui.Text("", "recipe__count"));
            mDetailDesc = dText.Append(Ui.Text("", "craft-detail__desc"));
            mDetailStation = dText.Append(Ui.Text("", "craft-detail__station"));
            mDetailInputs = detail.Append(Ui.Div("craft-detail__inputs"));
            var actions = detail.Append(Ui.Div("craft-actions"));
            mCraftBtn = actions.Append(new Button().Cls("btn", "btn--primary", "gap-r-12"));
            mCraftBtn.text = "Craft";
            mCraftBtn.style.minWidth = 150;
            mCraftBtn.RegisterCallback<ClickEvent>(e => CraftSelected(e.shiftKey));
            mCraftBtn.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
            mCraftAllBtn = actions.Append(Ui.Btn("Craft all", () => CraftSelected(true), UiSound.Click));
            actions.Add(Ui.Text("Shift-click Craft for as many as you can", "craft-hint"));

            // Floating layers: tooltip and the held stack follow the pointer.
            mTooltip = Root.Append(Ui.Div("tooltip").Ignore());
            mTipName = mTooltip.Append(Ui.Text("", "tooltip__name").Ignore());
            mTipKind = mTooltip.Append(Ui.Text("", "tooltip__kind").Ignore());
            mTipDur = mTooltip.Append(Ui.Div("tooltip__dur").Ignore());
            var bar = mTipDur.Append(Ui.Div("tooltip__dur-bar").Ignore());
            mTipDurFill = bar.Append(Ui.Div("tooltip__dur-fill").Ignore());
            mTipDurText = mTipDur.Append(Ui.Text("", "tooltip__dur-text").Ignore());
            mTipDesc = mTooltip.Append(Ui.Text("", "tooltip__desc").Ignore());

            mCursorEl = Root.Append(Ui.Div("cursor-stack").Ignore());
            mCursorIcon = mCursorEl.Append(Ui.Div("cursor-stack__icon").Ignore());
            mCursorCount = mCursorEl.Append(Ui.Text("", "slot__count").Ignore());
            mCursorEl.style.display = DisplayStyle.None;

            // Releasing a dragged stack anywhere that is not a slot.
            Root.RegisterCallback<PointerUpEvent>(e => { if (e.button == 0) ReleaseNowhere(); });
            Root.RegisterCallback<PointerMoveEvent>(e => mPointer = Root.WorldToLocal(e.position));
            SetCategory("All");
        }

        static IEnumerable<string> Categories()
        {
            yield return "All";
            var seen = new HashSet<string>();
            foreach (var c in kCategoryOrder)
                foreach (var r in Crafting.Recipes)
                    if (CategoryOf(r) == c && seen.Add(c)) { yield return c; break; }
            foreach (var r in Crafting.Recipes)
                if (seen.Add(CategoryOf(r))) yield return CategoryOf(r);
        }

        static string CategoryOf(Recipe r) => string.IsNullOrEmpty(r.Category) ? "Other" : r.Category;

        VisualElement MakeSlot(int i)
        {
            var sv = new SlotView(i < Inventory.Hotbar ? i + 1 : 0) { Index = i };
            sv.Root.AddToClassList("inv-slot");
            mSlots[i] = sv;
            var el = sv.Root;
            el.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button > 1) return;
                SlotDown(i, e.button, e.shiftKey);
                mDownPos = e.position;
                e.StopPropagation();
            });
            el.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != 0) return;
                SlotUp(i);
                e.StopPropagation();
            });
            el.RegisterCallback<PointerEnterEvent>(_ => { mHover = i; el.EnableInClassList("inv-slot--target", !mCursor.IsEmpty); UiSound.Play(UiSound.Hover); });
            el.RegisterCallback<PointerLeaveEvent>(_ => { if (mHover == i) mHover = -1; el.RemoveFromClassList("inv-slot--target"); });
            return el;
        }

        RecipeRow MakeRow(VisualElement list, Recipe r)
        {
            var row = new RecipeRow { R = r };
            row.El = list.Append(Ui.Div("recipe"));
            row.Icon = row.El.Append(Ui.Div("recipe__icon").Ignore());
            var text = row.El.Append(Ui.Div("grow").Ignore());
            var nameRow = text.Append(Ui.Div("row").Ignore());
            nameRow.Add(Ui.Text(ItemDb.NameOf(r.Output), "recipe__name").Ignore());
            if (r.OutputCount > 1) nameRow.Add(Ui.Text($"×{r.OutputCount}", "recipe__count").Ignore());
            row.Inputs = text.Append(Ui.Div("recipe__inputs").Ignore());
            foreach (var (id, count) in r.Inputs)
            {
                var chip = row.Inputs.Append(Ui.Div("recipe__in").Ignore());
                var ic = chip.Append(Ui.Div("recipe__in-icon").Ignore());
                var t = chip.Append(Ui.Text("", "recipe__in-text").Ignore());
                row.In.Add((ic, t, id, count));
            }
            if (r.Station != Station.None) row.Badge = row.El.Append(Ui.Text(r.Station.ToString().ToUpperInvariant(), "recipe__badge").Ignore());
            row.El.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                UiSound.Play(UiSound.Click);
                Select(r);
                if (e.clickCount >= 2) CraftSelected(e.shiftKey);
            });
            row.El.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
            return row;
        }

        // ------------------------------------------------------------------ lifecycle

        public void Bind(PlayerController p)
        {
            if (mInv != null) mInv.Changed -= OnChanged;
            mPlayer = p;
            mInv = p != null ? p.Inventory : null;
            if (mInv != null) mInv.Changed += OnChanged;
            mCursor = ItemStack.Empty;
        }

        /// <summary>Called before showing: opened with E (crafting=false) or from a station.</summary>
        public void Prepare(bool crafting, Station station)
        {
            mOpenedAt = station;
            if (crafting && station != Station.None)
            {
                SetCategory("All");
                var first = Crafting.Recipes.Find(r => r.Station == station);
                if (first != null) Select(first, scroll: true);
            }
        }

        protected override void OnShow()
        {
            if (!mIconsReady)
            {
                mIconsReady = true;
                foreach (var row in mRows)
                {
                    ItemIcons.Set(row.Icon, row.R.Output);
                    foreach (var (icon, _, id, _) in row.In) ItemIcons.Set(icon, id);
                }
            }
            RefreshNear();
            if (Selected == null) Select(FirstCraftable() ?? Crafting.Recipes[0]);
            OnChanged();
            HideTooltip();
        }

        protected override void OnHide()
        {
            ReturnCursor();
            HideTooltip();
            mOpenedAt = Station.None;
            mHover = -1;
        }

        public override bool OnBack()
        {
            UI.Pop(this);
            return true;
        }

        void RefreshNear()
        {
            var w = VoxelWorld.Instance;
            mNear = w != null && mPlayer != null ? Crafting.StationsNear(w, mPlayer.transform.position) : new HashSet<Station>();
            // The station the player just used counts as nearby while this screen is open.
            if (mOpenedAt != Station.None) mNear.Add(mOpenedAt);
            mStations.Clear();
            foreach (var st in new[] { Station.Workbench, Station.Furnace })
            {
                bool on = mNear.Contains(st);
                var chip = mStations.Append(Ui.Div("chip", on ? "chip--ok" : "chip--off", "gap-r-8"));
                chip.Add(Ui.Div("chip__dot"));
                chip.Add(Ui.Text(on ? $"{st} nearby" : $"No {st.ToString().ToLowerInvariant()}"));
            }
        }

        void OnChanged()
        {
            if (mInv == null) return;
            for (int i = 0; i < Inventory.Size; i++)
            {
                mSlots[i].Set(mInv.Slots[i]);
                mSlots[i].Root.EnableInClassList("inv-slot--selected", i == mInv.Selected);
            }
            RefreshRecipes();
        }

        void RefreshRecipes()
        {
            if (mInv == null) return;
            foreach (var row in mRows)
            {
                bool can = Crafting.CanCraft(mInv, row.R, mNear);
                row.El.EnableInClassList("recipe--locked", !can);
                foreach (var (_, text, id, need) in row.In)
                {
                    int have = mInv.Count(id);
                    text.text = $"{Mathf.Min(have, 999)}/{need}";
                    text.EnableInClassList("have-ok", have >= need);
                    text.EnableInClassList("have-no", have < need);
                }
                if (row.Badge != null) row.Badge.EnableInClassList("recipe__badge--ok", mNear.Contains(row.R.Station));
            }
            RefreshDetail();
        }

        // ------------------------------------------------------------------ crafting

        public void SetCategory(string cat)
        {
            mCategory = cat;
            foreach (var kv in mCatTabs) kv.Value.EnableInClassList("pill--on", kv.Key == cat);
            foreach (var kv in mCatHeaders) kv.Value.style.display = cat == "All" || kv.Key == cat ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var row in mRows)
                row.El.style.display = cat == "All" || CategoryOf(row.R) == cat ? DisplayStyle.Flex : DisplayStyle.None;
            if (Selected != null && cat != "All" && CategoryOf(Selected) != cat)
            {
                var first = mRows.Find(r => CategoryOf(r.R) == cat);
                if (first != null) Select(first.R);
            }
            mRecipeScroll.scrollOffset = Vector2.zero;
        }

        Recipe FirstCraftable()
        {
            if (mInv == null) return null;
            foreach (var row in mRows) if (Crafting.CanCraft(mInv, row.R, mNear)) return row.R;
            return null;
        }

        public void Select(Recipe r, bool scroll = false)
        {
            Selected = r;
            foreach (var row in mRows)
            {
                row.El.EnableInClassList("recipe--on", row.R == r);
                if (row.R == r && scroll) mRecipeScroll.schedule.Execute(() => mRecipeScroll.ScrollTo(row.El)).ExecuteLater(50);
            }
            RefreshDetail();
        }

        void RefreshDetail()
        {
            var r = Selected;
            if (r == null || mInv == null) return;
            ItemIcons.Set(mDetailIcon, r.Output);
            mDetailName.text = ItemDb.NameOf(r.Output);
            mDetailCount.text = r.OutputCount > 1 ? $"×{r.OutputCount}" : "";
            mDetailDesc.text = ItemIcons.Description(r.Output);
            if (r.Station == Station.None) { mDetailStation.text = ""; mDetailStation.style.display = DisplayStyle.None; }
            else
            {
                bool near = mNear.Contains(r.Station);
                mDetailStation.style.display = DisplayStyle.Flex;
                mDetailStation.text = near ? $"✓ {r.Station} nearby" : $"Needs a {r.Station} nearby";
                mDetailStation.EnableInClassList("t-accent", near);
                mDetailStation.EnableInClassList("t-warn", !near);
            }
            mDetailInputs.Clear();
            foreach (var (id, need) in r.Inputs)
            {
                int have = mInv.Count(id);
                var chip = mDetailInputs.Append(Ui.Div("ingredient"));
                var ic = chip.Append(Ui.Div("ingredient__icon").Ignore());
                ItemIcons.Set(ic, id);
                chip.Add(Ui.Text(ItemDb.NameOf(id), "ingredient__name"));
                chip.Add(Ui.Text($"{have} / {need}", "ingredient__count", have >= need ? "have-ok" : "have-no"));
            }
            bool can = Crafting.CanCraft(mInv, r, mNear);
            int max = can ? MaxCrafts(r) : 0;
            mCraftBtn.SetEnabled(can);
            mCraftAllBtn.SetEnabled(max > 1);
            mCraftAllBtn.text = max > 1 ? $"Craft all  ×{max}" : "Craft all";
        }

        int MaxCrafts(Recipe r)
        {
            int n = int.MaxValue;
            foreach (var (id, need) in r.Inputs) n = Mathf.Min(n, mInv.Count(id) / Mathf.Max(1, need));
            return n == int.MaxValue ? 0 : Mathf.Min(n, 64);
        }

        /// <summary>Craft the selected recipe once, or as many times as possible.</summary>
        public int CraftSelected(bool max)
        {
            var r = Selected;
            if (r == null || mInv == null) return 0;
            int made = 0;
            int limit = max ? 64 : 1;
            while (made < limit && Crafting.Craft(mInv, r, mNear)) made++;
            if (made == 0) { UiSound.Play(UiSound.Error); return 0; }
            UiSound.Play(UiSound.Craft);
            UI.Hud.Toast(r.Output, made * r.OutputCount, null);
            FlashSlotsWith(r.Output);
            Debug.Log($"WM_UI crafted {r.Key} x{made}");
            return made;
        }

        void FlashSlotsWith(int id)
        {
            for (int i = 0; i < Inventory.Size; i++)
            {
                if (mInv.Slots[i].Id != id) continue;
                var el = mSlots[i].Root;
                el.AddToClassList("inv-slot--flash");
                el.schedule.Execute(() => el.RemoveFromClassList("inv-slot--flash")).ExecuteLater(260);
            }
        }

        // ------------------------------------------------------------------ slots

        /// <summary>Pointer pressed on a slot (button 0 = left, 1 = right).</summary>
        public void SlotDown(int i, int button, bool shift)
        {
            if (mInv == null) return;
            ref var slot = ref mInv.Slots[i];
            mDragArmed = false;
            if (button == 0 && shift && mCursor.IsEmpty) { QuickMove(i); return; }
            if (button == 0)
            {
                if (mCursor.IsEmpty)
                {
                    if (slot.IsEmpty) return;
                    mCursor = slot; slot = ItemStack.Empty;
                    mDragFrom = i; mDragArmed = true;
                    UiSound.Play(UiSound.Click);
                }
                else Place(i, mCursor.Count);
            }
            else
            {
                if (mCursor.IsEmpty)
                {
                    if (slot.IsEmpty) return;
                    int take = (slot.Count + 1) / 2;
                    mCursor = slot; mCursor.Count = take;
                    slot.Count -= take;
                    if (slot.Count <= 0) slot = ItemStack.Empty;
                    UiSound.Play(UiSound.Click);
                }
                else Place(i, 1);
            }
            Changed();
        }

        /// <summary>Pointer released over a slot: completes a drag that started elsewhere.</summary>
        public void SlotUp(int i)
        {
            if (!mDragArmed || mCursor.IsEmpty) { mDragArmed = false; return; }
            mDragArmed = false;
            if (i == mDragFrom) return; // a plain click: keep holding the stack
            Place(i, mCursor.Count);
            Changed();
        }

        void ReleaseNowhere()
        {
            if (!mDragArmed || mCursor.IsEmpty) { mDragArmed = false; return; }
            mDragArmed = false;
            if ((mPointerScreen() - mDownPos).sqrMagnitude < 100f) return;
            // Dragged out of the panels: drop into the world; dragged onto panel chrome: put it back.
            if (OverPanels()) ReturnCursor();
            else DropCursor(false);
        }

        Vector2 mPointerScreen() => Root.LocalToWorld(mPointer);

        bool OverPanels()
        {
            var picked = Root.panel?.Pick(Root.LocalToWorld(mPointer));
            for (var e = picked; e != null; e = e.parent) if (e.ClassListContains("panel")) return true;
            return false;
        }

        /// <summary>Put up to <paramref name="amount"/> of the held stack into slot i (merge, fill or swap).</summary>
        void Place(int i, int amount)
        {
            ref var slot = ref mInv.Slots[i];
            int max = Inventory.MaxStack(mCursor.Id);
            if (slot.IsEmpty)
            {
                int n = Mathf.Min(amount, mCursor.Count);
                slot = mCursor; slot.Count = n;
                mCursor.Count -= n;
            }
            else if (slot.Id == mCursor.Id && max > 1)
            {
                int n = Mathf.Min(Mathf.Min(amount, mCursor.Count), max - slot.Count);
                if (n <= 0) { UiSound.Play(UiSound.Error); return; }
                slot.Count += n; mCursor.Count -= n;
            }
            else
            {
                (slot, mCursor) = (mCursor, slot);
            }
            if (mCursor.Count <= 0) mCursor = ItemStack.Empty;
            UiSound.Play(UiSound.Click);
        }

        /// <summary>Shift-click: hotbar slots move into the pack and vice versa (merging first).</summary>
        public void QuickMove(int i)
        {
            ref var src = ref mInv.Slots[i];
            if (src.IsEmpty) return;
            bool fromHotbar = i < Inventory.Hotbar;
            int a = fromHotbar ? Inventory.Hotbar : 0, b = fromHotbar ? Inventory.Size : Inventory.Hotbar;
            int max = Inventory.MaxStack(src.Id);
            if (max > 1)
                for (int k = a; k < b && src.Count > 0; k++)
                {
                    ref var d = ref mInv.Slots[k];
                    if (d.Id != src.Id || d.Count >= max) continue;
                    int n = Mathf.Min(max - d.Count, src.Count);
                    d.Count += n; src.Count -= n;
                }
            for (int k = a; k < b && src.Count > 0; k++)
            {
                ref var d = ref mInv.Slots[k];
                if (!d.IsEmpty) continue;
                d = src; src = ItemStack.Empty;
            }
            if (src.Count <= 0) src = ItemStack.Empty;
            UiSound.Play(UiSound.Click);
            Changed();
        }

        /// <summary>Drop the held stack (or one of it) into the world in front of the player.</summary>
        public void DropCursor(bool one)
        {
            if (mCursor.IsEmpty || mPlayer == null) return;
            var drop = mCursor;
            drop.Count = one ? 1 : mCursor.Count;
            mCursor.Count -= drop.Count;
            if (mCursor.Count <= 0) mCursor = ItemStack.Empty;
            SpawnDrop(drop);
            UiSound.Play(UiSound.Close);
            Changed();
        }

        void SpawnDrop(ItemStack s)
        {
            ItemDrop.Spawn(s, mPlayer.EyePosition + mPlayer.LookDir * 0.6f - Vector3.up * 0.3f, mPlayer.LookDir * 5f + Vector3.up * 1.5f, 1.5f);
        }

        void DropFromSlot(int i, bool all)
        {
            ref var s = ref mInv.Slots[i];
            if (s.IsEmpty) return;
            var drop = s; drop.Count = all ? s.Count : 1;
            s.Count -= drop.Count;
            if (s.Count <= 0) s = ItemStack.Empty;
            SpawnDrop(drop);
            Changed();
        }

        /// <summary>Put the held stack back (origin slot first, then anywhere; leftovers are dropped).</summary>
        void ReturnCursor()
        {
            if (mCursor.IsEmpty || mInv == null) { mCursor = ItemStack.Empty; return; }
            if (mDragFrom >= 0 && mDragFrom < Inventory.Size && mInv.Slots[mDragFrom].IsEmpty)
            {
                mInv.Slots[mDragFrom] = mCursor;
                mCursor = ItemStack.Empty;
            }
            else
            {
                var d = mCursor.Def;
                int left = mInv.Add(mCursor.Id, mCursor.Count, d != null && d.Durability > 0 ? mCursor.Durability : -1);
                if (left > 0 && mPlayer != null) { var rest = mCursor; rest.Count = left; SpawnDrop(rest); }
                mCursor = ItemStack.Empty;
            }
            Changed();
        }

        void Changed()
        {
            mInv?.Notify();
            SyncCursor();
        }

        void SyncCursor()
        {
            bool held = !mCursor.IsEmpty;
            mCursorEl.style.display = held ? DisplayStyle.Flex : DisplayStyle.None;
            if (held)
            {
                ItemIcons.Set(mCursorIcon, mCursor.Id);
                mCursorCount.text = mCursor.Count > 1 ? mCursor.Count.ToString() : "";
                HideTooltip();
            }
            if (mHover >= 0) mSlots[mHover].Root.EnableInClassList("inv-slot--target", held);
        }

        // ------------------------------------------------------------------ tooltip & frame

        void HideTooltip() => mTooltip?.RemoveFromClassList("tooltip--on");

        void ShowTooltip(ItemStack s)
        {
            var d = s.Def;
            mTipName.text = ItemDb.NameOf(s.Id);
            mTipKind.text = ItemIcons.Kind(d);
            string desc = ItemIcons.Description(s.Id);
            mTipDesc.text = desc;
            mTipDesc.style.display = desc.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            bool dur = d != null && d.Durability > 0;
            mTipDur.style.display = dur ? DisplayStyle.Flex : DisplayStyle.None;
            if (dur)
            {
                float f = Mathf.Clamp01(s.Durability / (float)d.Durability);
                mTipDurFill.style.width = Length.Percent(f * 100f);
                mTipDurFill.style.backgroundColor = SlotView.DurabilityColor(f);
                mTipDurText.text = $"{s.Durability} / {d.Durability}";
            }
            mTooltip.AddToClassList("tooltip--on");
        }

        int mTipFor = -2;
        Vector2? mDebugPointer;

        /// <summary>Harness: pretend the pointer hovers slot i (-1 = release), for screenshots.</summary>
        public void DebugHover(int i)
        {
            if (i < 0) { mDebugPointer = null; mHover = -1; return; }
            var r = mSlots[i].Root.worldBound;
            mDebugPointer = Root.WorldToLocal(new Vector2(r.center.x + 6f, r.center.y + 4f));
            mHover = i;
            mSlots[i].Root.EnableInClassList("inv-slot--target", !mCursor.IsEmpty);
        }

        public override void Tick(float dt)
        {
            if (mInv == null) return;
            // Pointer position from the OS cursor (also valid before the first move event).
            if (mDebugPointer.HasValue) mPointer = mDebugPointer.Value;
            else if (Root.panel != null)
            {
                var mp = Input.mousePosition;
                mPointer = Root.WorldToLocal(RuntimePanelUtils.ScreenToPanel(Root.panel, new Vector2(mp.x, Screen.height - mp.y)));
            }
            mCursorEl.style.left = mPointer.x - 30f;
            mCursorEl.style.top = mPointer.y - 30f;

            int tipFor = mCursor.IsEmpty && mHover >= 0 && !mInv.Slots[mHover].IsEmpty ? mHover : -1;
            if (tipFor != mTipFor)
            {
                mTipFor = tipFor;
                if (tipFor >= 0) ShowTooltip(mInv.Slots[tipFor]); else HideTooltip();
            }
            if (tipFor >= 0)
            {
                float w = Root.layout.width, h = Root.layout.height;
                float tw = mTooltip.layout.width, th = mTooltip.layout.height;
                float x = mPointer.x + 22f, y = mPointer.y + 22f;
                if (!float.IsNaN(tw) && x + tw > w - 12f) x = mPointer.x - tw - 16f;
                if (!float.IsNaN(th) && y + th > h - 12f) y = mPointer.y - th - 16f;
                mTooltip.style.left = x; mTooltip.style.top = y;
            }

            // Keyboard: hover + number swaps with the hotbar, hover + drop key drops.
            if (mHover >= 0 && mCursor.IsEmpty)
            {
                for (int k = 0; k < Inventory.Hotbar; k++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + k) && k != mHover) { mInv.Swap(mHover, k); UiSound.Play(UiSound.Click); mTipFor = -2; break; }
                if (GameSettings.Pressed(Control.Drop))
                {
                    bool all = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.RightCommand);
                    DropFromSlot(mHover, all);
                    mTipFor = -2;
                }
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                CraftSelected(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));

            mNearTimer -= dt;
            if (mNearTimer <= 0f)
            {
                mNearTimer = 0.5f;
                var before = new HashSet<Station>(mNear);
                RefreshNear();
                if (!before.SetEquals(mNear)) RefreshRecipes();
            }
        }
    }
}
