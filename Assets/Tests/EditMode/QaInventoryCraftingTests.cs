using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Tests
{
    /// <summary>QA: inventory stacking/removal/fit rules, tool wear, JSON persistence of slots.</summary>
    public class QaInventoryTests
    {
        static ItemStack[] Snapshot(Inventory inv) => (ItemStack[])inv.Slots.Clone();

        static void AssertSame(ItemStack[] a, Inventory inv, string ctx)
        {
            for (int i = 0; i < Inventory.Size; i++)
            {
                var b = inv.Slots[i];
                bool eq = (a[i].IsEmpty && b.IsEmpty) || (a[i].Id == b.Id && a[i].Count == b.Count && a[i].Durability == b.Durability);
                Assert.IsTrue(eq, $"{ctx}: slot {i} was {a[i].Id}x{a[i].Count}/{a[i].Durability}, now {b.Id}x{b.Count}/{b.Durability}");
            }
        }

        [Test]
        public void Add_StacksTo64_ThenOverflowsToNextSlot()
        {
            var inv = new Inventory();
            Assert.AreEqual(0, inv.Add(B.Stone, 100));
            Assert.AreEqual(64, inv.Slots[0].Count);
            Assert.AreEqual(36, inv.Slots[1].Count);
            Assert.AreEqual(100, inv.Count(B.Stone));
        }

        [Test]
        public void Add_FillsExistingStacksBeforeEmptySlots()
        {
            var inv = new Inventory();
            inv.Slots[5] = ItemStack.Of(B.Dirt, 10);
            inv.Add(B.Dirt, 60);
            Assert.AreEqual(64, inv.Slots[5].Count, "existing stack topped up first");
            Assert.AreEqual(B.Dirt, inv.Slots[0].Id, "remainder goes to the first empty slot");
            Assert.AreEqual(6, inv.Slots[0].Count);
        }

        [Test]
        public void Add_ReturnsLeftoverWhenFull()
        {
            var inv = new Inventory();
            Assert.AreEqual(5, inv.Add(B.Stone, 64 * Inventory.Size + 5));
            Assert.AreEqual(64 * Inventory.Size, inv.Count(B.Stone));
            Assert.AreEqual(3, inv.Add(B.Dirt, 3), "nothing fits in a full inventory");
        }

        [Test]
        public void Add_IgnoresEmptyAndNonPositive()
        {
            var inv = new Inventory();
            Assert.AreEqual(0, inv.Add(0, 5));
            Assert.AreEqual(0, inv.Add(B.Stone, 0));
            Assert.AreEqual(0, inv.Add(B.Stone, -4));
            Assert.IsTrue(inv.Slots.All(s => s.IsEmpty));
        }

        [Test]
        public void Tools_DoNotStack_AndStartAtFullDurability()
        {
            var inv = new Inventory();
            inv.Add(ItemIds.PickaxeWood, 3);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(ItemIds.PickaxeWood, inv.Slots[i].Id);
                Assert.AreEqual(1, inv.Slots[i].Count);
                Assert.AreEqual(ItemDb.Get(ItemIds.PickaxeWood).Durability, inv.Slots[i].Durability);
            }
            Assert.IsTrue(inv.Slots[3].IsEmpty);
            foreach (var d in ItemDb.All.Where(d => d.Durability > 0))
                Assert.AreEqual(1, d.MaxStack, $"{d.Name} has durability so must not stack");
        }

        [Test]
        public void Add_WithDurability_KeepsWear()
        {
            var inv = new Inventory();
            inv.Add(ItemIds.PickaxeIron, 1, 17);
            Assert.AreEqual(17, inv.Slots[0].Durability);
        }

        [Test]
        public void Remove_TakesFromTheBackAndReportsCount()
        {
            var inv = new Inventory();
            inv.Add(B.Stone, 100); // 64 + 36
            Assert.AreEqual(10, inv.Remove(B.Stone, 10));
            Assert.AreEqual(64, inv.Slots[0].Count);
            Assert.AreEqual(26, inv.Slots[1].Count);
            Assert.AreEqual(90, inv.Remove(B.Stone, 500), "only what exists is removed");
            Assert.IsTrue(inv.Slots[0].IsEmpty && inv.Slots[1].IsEmpty);
            Assert.AreEqual(0, inv.Remove(B.Dirt, 1));
        }

        [Test]
        public void CanFit_RespectsStackRoomAndTools()
        {
            var inv = new Inventory();
            inv.Add(B.Stone, 64 * Inventory.Size);
            Assert.IsFalse(inv.CanFit(B.Stone, 1));
            Assert.IsFalse(inv.CanFit(B.Dirt, 1));
            inv.Slots[7].Count = 63;
            Assert.IsTrue(inv.CanFit(B.Stone, 1));
            Assert.IsFalse(inv.CanFit(B.Stone, 2));
            Assert.IsFalse(inv.CanFit(B.Dirt, 1));
            inv.Slots[8] = ItemStack.Empty;
            Assert.IsTrue(inv.CanFit(ItemIds.AxeStone, 1));
            Assert.IsFalse(inv.CanFit(ItemIds.AxeStone, 2), "one empty slot holds one tool");
            Assert.IsTrue(inv.CanFit(B.Dirt, 64));
            Assert.IsFalse(inv.CanFit(B.Dirt, 65));
        }

        [Test]
        public void CanFit_AgreesWithAdd()
        {
            var rng = new System.Random(7);
            int[] ids = { B.Stone, B.Dirt, B.Torch, ItemIds.Stick, ItemIds.PickaxeWood, ItemIds.ShovelIron };
            for (int round = 0; round < 300; round++)
            {
                var inv = new Inventory();
                for (int i = 0; i < Inventory.Size; i++)
                    if (rng.NextDouble() < 0.85)
                    {
                        int id = ids[rng.Next(ids.Length)];
                        inv.Slots[i] = ItemStack.Of(id, 1 + rng.Next(Inventory.MaxStack(id)));
                    }
                int want = ids[rng.Next(ids.Length)], n = 1 + rng.Next(70);
                bool fits = inv.CanFit(want, n);
                int left = inv.Add(want, n);
                Assert.AreEqual(fits, left == 0, $"round {round}: CanFit({want},{n})={fits} but Add left {left}");
            }
        }

        [Test]
        public void ConsumeSelected_EmptiesSlotAtZero()
        {
            var inv = new Inventory();
            inv.Slots[2] = ItemStack.Of(B.Dirt, 2);
            inv.Selected = 2;
            Assert.IsTrue(inv.ConsumeSelected());
            Assert.IsTrue(inv.ConsumeSelected());
            Assert.IsTrue(inv.Slots[2].IsEmpty);
            Assert.IsFalse(inv.ConsumeSelected());
        }

        [Test]
        public void DamageSelected_WearsAndBreaksTools_IgnoresBlocks()
        {
            var inv = new Inventory();
            inv.Add(ItemIds.PickaxeWood, 1);
            inv.Selected = 0;
            int full = inv.Slots[0].Durability;
            Assert.IsFalse(inv.DamageSelected());
            Assert.AreEqual(full - 1, inv.Slots[0].Durability);
            inv.Slots[0].Durability = 1;
            Assert.IsTrue(inv.DamageSelected(), "last use breaks the tool");
            Assert.IsTrue(inv.Slots[0].IsEmpty);

            inv.Slots[0] = ItemStack.Of(B.Dirt, 5);
            Assert.IsFalse(inv.DamageSelected());
            Assert.AreEqual(5, inv.Slots[0].Count);
        }

        [Test]
        public void Changed_FiresOnMutation()
        {
            var inv = new Inventory();
            int n = 0;
            inv.Changed += () => n++;
            inv.Add(B.Stone, 1);
            inv.Remove(B.Stone, 1);
            inv.Swap(0, 1);
            inv.Clear();
            Assert.GreaterOrEqual(n, 4);
        }

        [Test, Category("KnownDefect")]
        public void Add_ThatAddsNothing_DoesNotRaiseChanged()
        {
            // QA-D10: BlockInteraction calls TryCollect -> Inventory.Add every frame while the player stands on a drop
            // that does not fit; Add always calls Notify(), so HUD/inventory screen refresh ~fps times per second.
            var inv = new Inventory();
            inv.Add(B.Stone, 64 * Inventory.Size);
            int n = 0;
            inv.Changed += () => n++;
            Assert.AreEqual(1, inv.Add(B.Dirt, 1));
            Assert.AreEqual(0, n, "Changed fired although nothing changed");
        }

        [Test]
        public void Json_RoundTripPreservesSlotsCountsDurability()
        {
            var inv = new Inventory();
            inv.Slots[0] = ItemStack.Of(B.Torch, 12);
            inv.Slots[8] = ItemStack.Of(ItemIds.PickaxeStone, 1); inv.Slots[8].Durability = 77;
            inv.Slots[20] = ItemStack.Of(ItemIds.RawIron, 3);
            inv.Slots[35] = ItemStack.Of(B.Furnace, 1);
            var snap = Snapshot(inv);
            string text = Json.Write(new Dictionary<string, object> { { "inv", inv.ToJson() } }, true);
            var back = new Inventory();
            back.FromJson(Json.Arr(Json.Parse(text), "inv"));
            AssertSame(snap, back, "round trip");
        }

        [Test]
        public void FromJson_SkipsUnknownIdsAndBadSlots()
        {
            var l = new List<object>
            {
                new Dictionary<string, object> { { "slot", 0.0 }, { "id", 99999.0 }, { "count", 3.0 } },
                new Dictionary<string, object> { { "slot", 40.0 }, { "id", (double)B.Stone }, { "count", 3.0 } },
                new Dictionary<string, object> { { "slot", -1.0 }, { "id", (double)B.Stone }, { "count", 3.0 } },
                new Dictionary<string, object> { { "slot", 4.0 }, { "id", (double)B.Dirt }, { "count", 9.0 } },
            };
            var inv = new Inventory();
            inv.Add(B.Glass, 5);
            inv.FromJson(l);
            Assert.AreEqual(0, inv.Count(B.Glass), "FromJson replaces the old contents");
            Assert.AreEqual(9, inv.Slots[4].Count);
            Assert.AreEqual(9, inv.Slots.Sum(s => s.Count));
        }
    }

    /// <summary>QA: recipe table integrity, station gating, exact crafting and full-inventory rollback.</summary>
    public class QaCraftingTests
    {
        static readonly HashSet<Station> None = new HashSet<Station>();
        static readonly HashSet<Station> All = new HashSet<Station> { Station.Workbench, Station.Furnace };

        [Test]
        public void Recipes_AreWellFormed()
        {
            Assert.Greater(Crafting.Recipes.Count, 10);
            var keys = new HashSet<string>();
            foreach (var r in Crafting.Recipes)
            {
                Assert.IsFalse(string.IsNullOrEmpty(r.Key), "recipe without key");
                Assert.IsTrue(keys.Add(r.Key), $"duplicate recipe key {r.Key}");
                Assert.IsTrue(ItemDb.Exists(r.Output), $"{r.Key}: output {r.Output} is not an item");
                Assert.Greater(r.OutputCount, 0, r.Key);
                Assert.LessOrEqual(r.OutputCount, Inventory.MaxStack(r.Output), $"{r.Key}: output count above max stack");
                Assert.IsFalse(string.IsNullOrEmpty(r.Category), $"{r.Key}: no category");
                Assert.IsNotNull(r.Inputs, r.Key);
                Assert.Greater(r.Inputs.Length, 0, r.Key);
                var seen = new HashSet<int>();
                foreach (var (id, count) in r.Inputs)
                {
                    Assert.IsTrue(ItemDb.Exists(id), $"{r.Key}: input {id} is not an item");
                    Assert.Greater(count, 0, $"{r.Key}: input {id} count");
                    Assert.IsTrue(seen.Add(id), $"{r.Key}: input {id} listed twice");
                    Assert.AreNotEqual(r.Output, id, $"{r.Key}: consumes its own output");
                }
            }
        }

        [Test]
        public void EveryToolAndStationIsCraftable()
        {
            var outputs = new HashSet<int>(Crafting.Recipes.Select(r => r.Output));
            foreach (var d in ItemDb.All.Where(d => d.Durability > 0))
                Assert.IsTrue(outputs.Contains(d.Id), $"{d.Name} has no recipe");
            Assert.IsTrue(outputs.Contains(B.Workbench) && outputs.Contains(B.Furnace) && outputs.Contains(B.Torch));
        }

        [Test]
        public void EveryRecipe_CraftsExactlyOnce_WithExactInputs()
        {
            foreach (var r in Crafting.Recipes)
            {
                var inv = new Inventory();
                foreach (var (id, count) in r.Inputs) inv.Add(id, count);
                Assert.IsTrue(Crafting.Craft(inv, r, All), $"{r.Key} failed with exact inputs");
                Assert.AreEqual(r.OutputCount, inv.Count(r.Output), $"{r.Key} output count");
                foreach (var (id, _) in r.Inputs) Assert.AreEqual(0, inv.Count(id), $"{r.Key} left input {id}");
                Assert.IsFalse(Crafting.Craft(inv, r, All), $"{r.Key} crafted twice from one set of inputs");
            }
        }

        [Test]
        public void StationRecipes_NeedTheirStation()
        {
            foreach (var r in Crafting.Recipes)
            {
                var inv = new Inventory();
                foreach (var (id, count) in r.Inputs) inv.Add(id, count);
                if (r.Station == Station.None)
                {
                    Assert.IsTrue(Crafting.CanCraft(inv, r, None), $"{r.Key} should be hand-craftable");
                    Assert.IsTrue(Crafting.CanCraft(inv, r, null), $"{r.Key} with null station set");
                    continue;
                }
                Assert.IsFalse(Crafting.Craft(inv, r, None), $"{r.Key} crafted without {r.Station}");
                Assert.IsFalse(Crafting.Craft(inv, r, null), $"{r.Key} crafted with null stations");
                var other = new HashSet<Station> { r.Station == Station.Workbench ? Station.Furnace : Station.Workbench };
                Assert.IsFalse(Crafting.Craft(inv, r, other), $"{r.Key} crafted at the wrong station");
                foreach (var (id, count) in r.Inputs) Assert.AreEqual(count, inv.Count(id), $"{r.Key}: failed craft consumed inputs");
                Assert.IsTrue(Crafting.Craft(inv, r, new HashSet<Station> { r.Station }), $"{r.Key} failed at its station");
            }
        }

        [Test]
        public void MissingInputs_ChangeNothing()
        {
            foreach (var r in Crafting.Recipes)
            {
                var inv = new Inventory();
                var (first, firstCount) = r.Inputs[0];
                foreach (var (id, count) in r.Inputs) inv.Add(id, id == first ? count - 1 : count);
                int before = inv.Slots.Sum(s => s.Count);
                Assert.IsFalse(Crafting.Craft(inv, r, All), r.Key);
                Assert.AreEqual(before, inv.Slots.Sum(s => s.Count), r.Key);
                _ = firstCount;
            }
        }

        [Test]
        public void FullInventory_RollsBackExactly()
        {
            foreach (var r in Crafting.Recipes)
            {
                // Every slot full; the inputs sit in full stacks so consuming them frees no slot.
                var inv = new Inventory();
                int slot = 0;
                foreach (var (id, count) in r.Inputs)
                {
                    int max = Inventory.MaxStack(id);
                    inv.Slots[slot++] = ItemStack.Of(id, max);
                    if (max < count) Assert.Inconclusive($"{r.Key}: input stack smaller than recipe count");
                }
                int filler = r.Output == B.Gravel ? B.Clay : B.Gravel;
                while (slot < Inventory.Size) inv.Slots[slot++] = ItemStack.Of(filler, 64);
                var snap = (ItemStack[])inv.Slots.Clone();
                Assert.IsFalse(Crafting.Craft(inv, r, All), $"{r.Key} crafted into a full inventory");
                for (int i = 0; i < Inventory.Size; i++)
                    Assert.IsTrue(snap[i].Id == inv.Slots[i].Id && snap[i].Count == inv.Slots[i].Count,
                        $"{r.Key}: slot {i} changed {snap[i].Id}x{snap[i].Count} -> {inv.Slots[i].Id}x{inv.Slots[i].Count}");
            }
        }

        [Test]
        public void PartialFit_RollsBackExactly()
        {
            var r = Crafting.Recipes.Find(x => x.Key == "planks_" + B.LogOak);
            var inv = new Inventory();
            inv.Slots[0] = ItemStack.Of(B.Planks, 62);       // room for 2 of the 4 planks
            inv.Slots[1] = ItemStack.Of(B.LogOak, 64);
            for (int i = 2; i < Inventory.Size; i++) inv.Slots[i] = ItemStack.Of(B.Gravel, 64);
            Assert.IsFalse(Crafting.Craft(inv, r, None));
            Assert.AreEqual(62, inv.Slots[0].Count);
            Assert.AreEqual(64, inv.Slots[1].Count);
            Assert.AreEqual(64 * 34, inv.Count(B.Gravel));
        }

        [Test]
        public void ConsumingInputs_FreesRoomForOutput()
        {
            // A single log in an otherwise full inventory still becomes planks (the freed slot takes them).
            var r = Crafting.Recipes.Find(x => x.Key == "planks_" + B.LogOak);
            var inv = new Inventory();
            inv.Slots[0] = ItemStack.Of(B.LogOak, 1);
            for (int i = 1; i < Inventory.Size; i++) inv.Slots[i] = ItemStack.Of(B.Gravel, 64);
            Assert.IsTrue(Crafting.Craft(inv, r, None));
            Assert.AreEqual(4, inv.Count(B.Planks));
        }

        [Test]
        public void StarterProgression_IsReachable()
        {
            // Logs -> planks -> sticks -> workbench -> wooden pickaxe -> (cobble) stone pickaxe -> furnace.
            var inv = new Inventory();
            inv.Add(B.LogOak, 4);
            var R = Crafting.Recipes.ToDictionary(x => x.Key);
            for (int i = 0; i < 4; i++) Assert.IsTrue(Crafting.Craft(inv, R["planks_" + B.LogOak], None));
            Assert.IsTrue(Crafting.Craft(inv, R["workbench"], None));
            Assert.IsTrue(Crafting.Craft(inv, R["stick"], None));
            Assert.IsTrue(Crafting.Craft(inv, R["pickaxe_wood"], new HashSet<Station> { Station.Workbench }));
            inv.Add(B.Cobblestone, 11);
            Assert.IsTrue(Crafting.Craft(inv, R["stick"], None));
            Assert.IsTrue(Crafting.Craft(inv, R["pickaxe_stone"], new HashSet<Station> { Station.Workbench }));
            Assert.IsTrue(Crafting.Craft(inv, R["furnace"], new HashSet<Station> { Station.Workbench }));
            Assert.AreEqual(1, inv.Count(ItemIds.PickaxeStone));
            Assert.AreEqual(1, inv.Count(B.Furnace));
        }
    }

    /// <summary>QA: break times, harvest tiers and drops for every block.</summary>
    public class QaItemDbTests
    {
        static IEnumerable<BlockDef> AllBlocks() => Enumerable.Range(1, 255).Where(Blocks.Exists).Select(i => Blocks.Get(i));

        static ItemDef Tool(ToolKind k, int tier) =>
            ItemDb.All.First(d => d.Tool == k && d.Tier == tier && d.Durability > 0);

        [Test]
        public void EveryBlockExceptWaterHasAnItem_AndBlockItemsPlaceThemselves()
        {
            foreach (var b in AllBlocks())
            {
                if (b.Id == B.Water) { Assert.IsFalse(ItemDb.Exists(B.Water), "water should not be an item"); continue; }
                var it = ItemDb.Get(b.Id);
                Assert.IsNotNull(it, b.Name);
                Assert.AreEqual(b.Id, it.PlacesBlock, b.Name);
            }
            foreach (var it in ItemDb.All)
                if (it.PlacesBlock != 0) Assert.IsTrue(Blocks.Exists(it.PlacesBlock), it.Name);
        }

        [Test]
        public void Unbreakables_AreInfinite_BreakablesFinite()
        {
            Assert.IsTrue(float.IsPositiveInfinity(ItemDb.BreakTime(Blocks.Get(B.Bedrock), null)));
            Assert.IsTrue(float.IsPositiveInfinity(ItemDb.BreakTime(Blocks.Get(B.Bedrock), Tool(ToolKind.Pickaxe, 3))));
            Assert.IsTrue(float.IsPositiveInfinity(ItemDb.BreakTime(Blocks.Get(B.Water), null)));
            foreach (var b in AllBlocks())
            {
                if (b.Hardness < 0) continue;
                float t = ItemDb.BreakTime(b, null);
                Assert.IsTrue(t > 0 && !float.IsInfinity(t) && t < 60f, $"{b.Name} hand time {t}");
            }
        }

        [Test]
        public void MatchingTool_IsFaster_HigherTierNotSlower_WrongToolSameAsHand()
        {
            foreach (var b in AllBlocks())
            {
                if (b.Hardness <= 0) continue;
                float hand = ItemDb.BreakTime(b, null);
                foreach (ToolKind k in new[] { ToolKind.Pickaxe, ToolKind.Axe, ToolKind.Shovel })
                {
                    float prev = float.MaxValue;
                    for (int tier = 1; tier <= 3; tier++)
                    {
                        var t = ItemDb.BreakTime(b, Tool(k, tier));
                        if (k == b.Tool)
                        {
                            Assert.Less(t, hand, $"{b.Name}: {k} tier {tier} not faster than hand");
                            Assert.LessOrEqual(t, prev + 1e-5f, $"{b.Name}: {k} tier {tier} slower than tier {tier - 1}");
                            prev = t;
                        }
                        else if (b.MinTier == 0)
                            Assert.AreEqual(hand, t, 1e-5f, $"{b.Name}: wrong tool {k} changed break time");
                    }
                }
                // Non-tool items behave like the hand.
                Assert.AreEqual(hand, ItemDb.BreakTime(b, ItemDb.Get(ItemIds.Stick)), 1e-5f, b.Name);
                Assert.AreEqual(hand, ItemDb.BreakTime(b, ItemDb.Get(B.Dirt)), 1e-5f, b.Name);
            }
        }

        [Test]
        public void KnownBreakTimes()
        {
            Assert.AreEqual(7.5f, ItemDb.BreakTime(Blocks.Get(B.Stone), null), 1e-4f, "stone by hand");
            Assert.AreEqual(1.125f, ItemDb.BreakTime(Blocks.Get(B.Stone), Tool(ToolKind.Pickaxe, 1)), 1e-4f);
            Assert.AreEqual(0.375f, ItemDb.BreakTime(Blocks.Get(B.Stone), Tool(ToolKind.Pickaxe, 3)), 1e-4f);
            Assert.AreEqual(0.05f, ItemDb.BreakTime(Blocks.Get(B.TallGrass), null), 1e-4f);
            Assert.AreEqual(0.05f, ItemDb.BreakTime(Blocks.Get(B.Torch), null), 1e-4f);
            Assert.AreEqual(3f, ItemDb.BreakTime(Blocks.Get(B.LogOak), null), 1e-4f);
            Assert.AreEqual(1.5f, ItemDb.BreakTime(Blocks.Get(B.LogOak), Tool(ToolKind.Axe, 1)), 1e-4f);
        }

        [Test]
        public void HarvestTiers()
        {
            var hand = (ItemDef)null;
            Assert.IsFalse(ItemDb.CanHarvest(Blocks.Get(B.Stone), hand));
            Assert.IsFalse(ItemDb.CanHarvest(Blocks.Get(B.Stone), Tool(ToolKind.Axe, 3)));
            Assert.IsTrue(ItemDb.CanHarvest(Blocks.Get(B.Stone), Tool(ToolKind.Pickaxe, 1)));
            Assert.IsFalse(ItemDb.CanHarvest(Blocks.Get(B.IronOre), Tool(ToolKind.Pickaxe, 1)));
            Assert.IsTrue(ItemDb.CanHarvest(Blocks.Get(B.IronOre), Tool(ToolKind.Pickaxe, 2)));
            Assert.IsFalse(ItemDb.CanHarvest(Blocks.Get(B.GoldOre), Tool(ToolKind.Pickaxe, 2)));
            Assert.IsTrue(ItemDb.CanHarvest(Blocks.Get(B.GoldOre), Tool(ToolKind.Pickaxe, 3)));
            Assert.IsTrue(ItemDb.CanHarvest(Blocks.Get(B.Dirt), hand));
            foreach (var b in AllBlocks())
                if (b.MinTier > 0) Assert.AreEqual(ToolKind.Pickaxe, b.Tool, $"{b.Name} needs a tier but its tool is {b.Tool}");
        }

        [Test]
        public void Drops()
        {
            var rng = new System.Random(3);
            var pick = Tool(ToolKind.Pickaxe, 3);
            Assert.AreEqual(B.Cobblestone, ItemDb.DropOf(Blocks.Get(B.Stone), pick, rng));
            Assert.AreEqual(0, ItemDb.DropOf(Blocks.Get(B.Stone), null, rng));
            Assert.AreEqual(B.Dirt, ItemDb.DropOf(Blocks.Get(B.Grass), null, rng));
            Assert.AreEqual(B.Dirt, ItemDb.DropOf(Blocks.Get(B.Podzol), null, rng));
            Assert.AreEqual(B.Dirt, ItemDb.DropOf(Blocks.Get(B.SnowyGrass), null, rng));
            Assert.AreEqual(0, ItemDb.DropOf(Blocks.Get(B.Glass), pick, rng));
            Assert.AreEqual(0, ItemDb.DropOf(Blocks.Get(B.Ice), pick, rng));
            Assert.AreEqual(0, ItemDb.DropOf(Blocks.Get(B.TallGrass), null, rng));
            Assert.AreEqual(0, ItemDb.DropOf(Blocks.Get(B.Fern), null, rng));
            Assert.AreEqual(ItemIds.Coal, ItemDb.DropOf(Blocks.Get(B.CoalOre), pick, rng));
            Assert.AreEqual(ItemIds.RawIron, ItemDb.DropOf(Blocks.Get(B.IronOre), pick, rng));
            Assert.AreEqual(ItemIds.RawGold, ItemDb.DropOf(Blocks.Get(B.GoldOre), pick, rng));
            Assert.AreEqual(B.Furnace, ItemDb.DropOf(Blocks.Get(B.Furnace), pick, rng));
            Assert.AreEqual(B.Workbench, ItemDb.DropOf(Blocks.Get(B.Workbench), null, rng));
            Assert.AreEqual(B.Torch, ItemDb.DropOf(Blocks.Get(B.Torch), null, rng));
            Assert.AreEqual(B.FlowerRed, ItemDb.DropOf(Blocks.Get(B.FlowerRed), null, rng));
            int flint = 0, gravel = 0, sticks = 0, nothing = 0;
            for (int i = 0; i < 4000; i++)
            {
                int g = ItemDb.DropOf(Blocks.Get(B.Gravel), null, rng);
                if (g == ItemIds.Flint) flint++; else if (g == B.Gravel) gravel++; else Assert.Fail($"gravel dropped {g}");
                int l = ItemDb.DropOf(Blocks.Get(B.LeavesOak), null, rng);
                if (l == ItemIds.Stick) sticks++; else if (l == 0) nothing++; else Assert.Fail($"leaves dropped {l}");
            }
            Assert.That(flint, Is.InRange(250, 550), "flint ~10%");
            Assert.That(sticks, Is.InRange(140, 360), "sticks ~6%");
        }

        [Test]
        public void EveryDropIsARealItem()
        {
            var rng = new System.Random(5);
            var pick = Tool(ToolKind.Pickaxe, 3);
            foreach (var b in AllBlocks())
                for (int i = 0; i < 50; i++)
                {
                    int d = ItemDb.DropOf(b, pick, rng);
                    Assert.IsTrue(d == 0 || ItemDb.Exists(d), $"{b.Name} drops unknown item {d}");
                }
        }
    }
}
