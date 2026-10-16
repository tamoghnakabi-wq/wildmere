using System.Collections.Generic;
using UnityEngine;
using Wildmere.World;

namespace Wildmere.Items
{
    /// <summary>
    /// Item ids share one int space: 1..255 are block items (id == block id), 256+ are plain items.
    /// Values are part of the save format — append only.
    /// </summary>
    public static class ItemIds
    {
        public const int None = 0;
        public const int Stick = 256, Coal = 257, RawIron = 258, IronIngot = 259, RawGold = 260, GoldIngot = 261, Flint = 262;
        public const int PickaxeWood = 300, PickaxeStone = 301, PickaxeIron = 302;
        public const int AxeWood = 310, AxeStone = 311, AxeIron = 312;
        public const int ShovelWood = 320, ShovelStone = 321, ShovelIron = 322;
    }

    public sealed class ItemDef
    {
        public int Id;
        public string Name;
        public string Key;
        public int MaxStack = 64;
        public byte PlacesBlock;         // block id placed on use (0 = not placeable)
        public ToolKind Tool;            // for tools
        public int Tier;                 // 1 wood, 2 stone, 3 iron
        public float Speed = 1f;         // mining speed multiplier when the tool matches
        public int Durability;           // > 0 for tools
        public string Icon;              // Resources path of a sprite, or null = render the block
        public string Model;             // Resources path of a held model, or null
        public string Description;
    }

    public static class ItemDb
    {
        static readonly Dictionary<int, ItemDef> sDefs = new Dictionary<int, ItemDef>();
        public static IEnumerable<ItemDef> All => sDefs.Values;

        static ItemDb()
        {
            for (int b = 1; b < 256; b++)
            {
                if (!Blocks.Exists(b) || b == B.Water) continue;
                var bd = Blocks.Get(b);
                Add(new ItemDef { Id = b, Name = bd.Name, Key = bd.Key, PlacesBlock = (byte)b });
            }
            Add(new ItemDef { Id = ItemIds.Stick, Name = "Stick", Key = "stick", Icon = "Textures/Items/stick" });
            Add(new ItemDef { Id = ItemIds.Coal, Name = "Coal", Key = "coal", Icon = "Textures/Items/coal" });
            Add(new ItemDef { Id = ItemIds.RawIron, Name = "Raw Iron", Key = "raw_iron", Icon = "Textures/Items/raw_iron" });
            Add(new ItemDef { Id = ItemIds.IronIngot, Name = "Iron Ingot", Key = "iron_ingot", Icon = "Textures/Items/iron_ingot" });
            Add(new ItemDef { Id = ItemIds.RawGold, Name = "Raw Gold", Key = "raw_gold", Icon = "Textures/Items/raw_gold" });
            Add(new ItemDef { Id = ItemIds.GoldIngot, Name = "Gold Ingot", Key = "gold_ingot", Icon = "Textures/Items/gold_ingot" });
            Add(new ItemDef { Id = ItemIds.Flint, Name = "Flint", Key = "flint", Icon = "Textures/Items/flint" });

            Tool(ItemIds.PickaxeWood, "Wooden Pickaxe", ToolKind.Pickaxe, 1, 2f, 60, "Pickaxe_Wood");
            Tool(ItemIds.PickaxeStone, "Stone Pickaxe", ToolKind.Pickaxe, 2, 4f, 132, "Pickaxe_Stone");
            Tool(ItemIds.PickaxeIron, "Iron Pickaxe", ToolKind.Pickaxe, 3, 6f, 251, "Pickaxe_Iron");
            Tool(ItemIds.AxeWood, "Wooden Axe", ToolKind.Axe, 1, 2f, 60, "Axe_Wood");
            Tool(ItemIds.AxeStone, "Stone Axe", ToolKind.Axe, 2, 4f, 132, "Axe_Stone");
            Tool(ItemIds.AxeIron, "Iron Axe", ToolKind.Axe, 3, 6f, 251, "Axe_Iron");
            Tool(ItemIds.ShovelWood, "Wooden Shovel", ToolKind.Shovel, 1, 2f, 60, "Shovel_Wood");
            Tool(ItemIds.ShovelStone, "Stone Shovel", ToolKind.Shovel, 2, 4f, 132, "Shovel_Stone");
            Tool(ItemIds.ShovelIron, "Iron Shovel", ToolKind.Shovel, 3, 6f, 251, "Shovel_Iron");
        }

        static void Add(ItemDef d) => sDefs[d.Id] = d;

        static void Tool(int id, string name, ToolKind kind, int tier, float speed, int durability, string model)
        {
            Add(new ItemDef
            {
                Id = id, Name = name, Key = name.ToLowerInvariant().Replace(' ', '_'), MaxStack = 1, Tool = kind,
                Tier = tier, Speed = speed, Durability = durability, Icon = "Models/Icons/" + model,
                Model = "Models/Tools/" + model,
            });
        }

        public static ItemDef Get(int id) => id != 0 && sDefs.TryGetValue(id, out var d) ? d : null;
        public static bool Exists(int id) => sDefs.ContainsKey(id);
        public static string NameOf(int id) => Get(id)?.Name ?? "?";

        /// <summary>Seconds to break <paramref name="block"/> holding <paramref name="held"/> (null = hand). Infinity if unbreakable.</summary>
        public static float BreakTime(BlockDef block, ItemDef held)
        {
            if (block.Hardness < 0f) return float.PositiveInfinity;
            if (block.Hardness == 0f) return 0.05f;
            bool matches = held != null && held.Tool != ToolKind.None && held.Tool == block.Tool;
            float speed = matches ? held.Speed : 1f;
            bool harvest = CanHarvest(block, held);
            return block.Hardness * (harvest ? 1.5f : 5f) / speed;
        }

        /// <summary>Whether breaking the block yields its drop with this tool.</summary>
        public static bool CanHarvest(BlockDef block, ItemDef held)
        {
            if (block.MinTier <= 0) return true;
            return held != null && held.Tool == ToolKind.Pickaxe && held.Tier >= block.MinTier;
        }

        /// <summary>The item a broken block drops (0 = nothing).</summary>
        public static int DropOf(BlockDef block, ItemDef held, System.Random rng)
        {
            if (!CanHarvest(block, held)) return 0;
            if (block.Id == B.Gravel && rng.NextDouble() < 0.1) return ItemIds.Flint;
            if (Blocks.IsLeaves(block.Id)) return rng.NextDouble() < 0.06 ? ItemIds.Stick : 0;
            if (block.Drop == 0) return 0;
            return block.Drop > 0 ? block.Drop : block.Id;
        }
    }
}
