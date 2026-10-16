using System.Collections.Generic;
using Wildmere.World;

namespace Wildmere.Items
{
    public enum Station { None, Workbench, Furnace }

    public sealed class Recipe
    {
        public string Key;
        public int Output;
        public int OutputCount;
        public (int id, int count)[] Inputs;
        public Station Station;
        public string Category;   // "Building", "Tools", "Materials", "Lighting"
    }

    /// <summary>
    /// Shapeless recipe crafting. Some recipes need a station (workbench / furnace) placed within
    /// <see cref="StationRange"/> blocks of the player.
    /// </summary>
    public static class Crafting
    {
        public const float StationRange = 4.5f;
        public static readonly List<Recipe> Recipes = new List<Recipe>();

        // Any log counts as "log" for the planks recipe; each log type gets its own entry instead.
        static Crafting()
        {
            foreach (var log in new[] { B.LogOak, B.LogBirch, B.LogSpruce, B.LogDark })
                Add("planks_" + log, B.Planks, 4, Station.None, "Materials", (log, 1));
            Add("stick", ItemIds.Stick, 4, Station.None, "Materials", (B.Planks, 2));
            Add("workbench", B.Workbench, 1, Station.None, "Building", (B.Planks, 4));
            Add("torch", B.Torch, 4, Station.None, "Lighting", (ItemIds.Coal, 1), (ItemIds.Stick, 1));
            Add("sandstone", B.Sandstone, 1, Station.None, "Building", (B.Sand, 4));

            Add("pickaxe_wood", ItemIds.PickaxeWood, 1, Station.Workbench, "Tools", (B.Planks, 3), (ItemIds.Stick, 2));
            Add("axe_wood", ItemIds.AxeWood, 1, Station.Workbench, "Tools", (B.Planks, 3), (ItemIds.Stick, 2));
            Add("shovel_wood", ItemIds.ShovelWood, 1, Station.Workbench, "Tools", (B.Planks, 1), (ItemIds.Stick, 2));
            Add("pickaxe_stone", ItemIds.PickaxeStone, 1, Station.Workbench, "Tools", (B.Cobblestone, 3), (ItemIds.Stick, 2));
            Add("axe_stone", ItemIds.AxeStone, 1, Station.Workbench, "Tools", (B.Cobblestone, 3), (ItemIds.Stick, 2));
            Add("shovel_stone", ItemIds.ShovelStone, 1, Station.Workbench, "Tools", (B.Cobblestone, 1), (ItemIds.Stick, 2));
            Add("pickaxe_iron", ItemIds.PickaxeIron, 1, Station.Workbench, "Tools", (ItemIds.IronIngot, 3), (ItemIds.Stick, 2));
            Add("axe_iron", ItemIds.AxeIron, 1, Station.Workbench, "Tools", (ItemIds.IronIngot, 3), (ItemIds.Stick, 2));
            Add("shovel_iron", ItemIds.ShovelIron, 1, Station.Workbench, "Tools", (ItemIds.IronIngot, 1), (ItemIds.Stick, 2));
            Add("furnace", B.Furnace, 1, Station.Workbench, "Building", (B.Cobblestone, 8));
            Add("mossy_cobblestone", B.MossyCobblestone, 4, Station.Workbench, "Building", (B.Cobblestone, 4), (B.Dirt, 1));

            Add("glass", B.Glass, 4, Station.Furnace, "Building", (B.Sand, 4), (ItemIds.Coal, 1));
            Add("stone", B.Stone, 4, Station.Furnace, "Building", (B.Cobblestone, 4), (ItemIds.Coal, 1));
            Add("iron_ingot", ItemIds.IronIngot, 2, Station.Furnace, "Materials", (ItemIds.RawIron, 2), (ItemIds.Coal, 1));
            Add("gold_ingot", ItemIds.GoldIngot, 2, Station.Furnace, "Materials", (ItemIds.RawGold, 2), (ItemIds.Coal, 1));
        }

        static void Add(string key, int output, int count, Station station, string category, params (int, int)[] inputs)
        {
            Recipes.Add(new Recipe { Key = key, Output = output, OutputCount = count, Inputs = inputs, Station = station, Category = category });
        }

        public static bool HasInputs(Inventory inv, Recipe r)
        {
            foreach (var (id, count) in r.Inputs)
                if (inv.Count(id) < count) return false;
            return true;
        }

        public static bool CanCraft(Inventory inv, Recipe r, ICollection<Station> nearby) =>
            (r.Station == Station.None || (nearby != null && nearby.Contains(r.Station))) && HasInputs(inv, r);

        /// <summary>Craft once. Returns false (and changes nothing) if inputs/station/space are missing.</summary>
        public static bool Craft(Inventory inv, Recipe r, ICollection<Station> nearby)
        {
            if (!CanCraft(inv, r, nearby)) return false;
            // Make sure the output fits after the inputs are gone (inputs free space, so check generously).
            foreach (var (id, count) in r.Inputs) inv.Remove(id, count);
            int left = inv.Add(r.Output, r.OutputCount);
            if (left > 0)
            {
                // Roll back: put the inputs back and remove what was added.
                inv.Remove(r.Output, r.OutputCount - left);
                foreach (var (id, count) in r.Inputs) inv.Add(id, count);
                return false;
            }
            return true;
        }

        /// <summary>Stations placed within range of a world position.</summary>
        public static HashSet<Station> StationsNear(VoxelWorld w, UnityEngine.Vector3 p)
        {
            var set = new HashSet<Station>();
            int r = UnityEngine.Mathf.CeilToInt(StationRange);
            int px = UnityEngine.Mathf.FloorToInt(p.x), py = UnityEngine.Mathf.FloorToInt(p.y), pz = UnityEngine.Mathf.FloorToInt(p.z);
            for (int y = py - 2; y <= py + 3; y++)
                for (int z = pz - r; z <= pz + r; z++)
                    for (int x = px - r; x <= px + r; x++)
                    {
                        byte b = w.GetBlock(x, y, z);
                        if (b == B.Workbench) set.Add(Station.Workbench);
                        else if (b == B.Furnace) set.Add(Station.Furnace);
                    }
            return set;
        }
    }
}
