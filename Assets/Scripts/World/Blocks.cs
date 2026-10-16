using System;
using UnityEngine;

namespace Wildmere.World
{
    /// <summary>Block ids. Stored as one byte per voxel; values are part of the save format.</summary>
    public static class B
    {
        public const byte Air = 0, Stone = 1, Dirt = 2, Grass = 3, Sand = 4, Sandstone = 5, Gravel = 6, Clay = 7,
            Snow = 8, Ice = 9, Bedrock = 10, Water = 11, Cobblestone = 12, MossyCobblestone = 13, Planks = 14,
            Glass = 15, LogOak = 16, LogBirch = 17, LogSpruce = 18, LogDark = 19, LeavesOak = 20, LeavesBirch = 21,
            LeavesSpruce = 22, LeavesDark = 23, CoalOre = 24, IronOre = 25, GoldOre = 26, Cactus = 27,
            Workbench = 28, Furnace = 29, Torch = 30, TallGrass = 31, Fern = 32, FlowerRed = 33, FlowerYellow = 34,
            FlowerBlue = 35, FlowerWhite = 36, DeadBush = 37, MushroomRed = 38, MushroomBrown = 39, Podzol = 40,
            SnowyGrass = 41, GlowCap = 42;

        public const int Count = 43;
    }

    public enum RenderShape : byte { None, Cube, Leaves, Glass, Cross, Liquid }

    /// <summary>Which tool speeds a block up.</summary>
    public enum ToolKind : byte { None, Pickaxe, Axe, Shovel }

    /// <summary>Material family used for footsteps, mining and placement sounds and break particles.</summary>
    public enum SoundMat : byte { Stone, Dirt, Grass, Sand, Gravel, Wood, Leaves, Glass, Snow, Water, Plant }

    public enum TintKind : byte { None, Grass, Foliage }

    public sealed class BlockDef
    {
        public byte Id;
        public string Name;          // display name
        public string Key;           // stable lowercase key (save/debug)
        public RenderShape Shape;
        public bool Solid;           // collides with entities
        public bool Opaque;          // hides neighbour faces and blocks sky light
        public byte LightEmit;       // 0..15
        public byte LightAbsorb;     // extra light loss when passing through (leaves/water)
        public float Hardness;       // seconds to break by hand when no tool is required; < 0 = unbreakable
        public ToolKind Tool;
        public int MinTier;          // pickaxe tier needed for a drop (0 = any/hand, 1 wood, 2 stone, 3 iron)
        public SoundMat Sound;
        public TintKind Tint;
        public bool Replaceable;     // plants/air/water: placing a block here overwrites it
        public bool NeedsSupport;    // plants/torches pop when the block below goes away
        public bool Emissive;        // shader flag
        public bool Wind;            // shader sway (leaves/plants)
        public string Top, Bottom, Side, Front; // texture layer names
        public int Drop = -1;        // item id dropped (-1 = itself, 0 = nothing)
        public Color32 Particle;     // break-particle colour (filled from textures when they load)

        // Resolved texture layer indices (filled by BlockTextures).
        public int LTop, LBottom, LSide, LFront;
    }

    public static class Blocks
    {
        public static readonly BlockDef[] Defs = new BlockDef[256];

        // Hot-path lookup tables (indexed by id) used by the mesher and physics.
        public static readonly bool[] IsOpaque = new bool[256];
        public static readonly bool[] IsSolid = new bool[256];
        public static readonly RenderShape[] ShapeOf = new RenderShape[256];
        public static readonly byte[] Emit = new byte[256];
        public static readonly byte[] Absorb = new byte[256];

        static Blocks()
        {
            Cube(B.Stone, "Stone", "stone", 1.5f, ToolKind.Pickaxe, 1, SoundMat.Stone, drop: B.Cobblestone);
            Cube(B.Dirt, "Dirt", "dirt", 0.5f, ToolKind.Shovel, 0, SoundMat.Dirt);
            Cube(B.Grass, "Grass Block", "grass_side", 0.6f, ToolKind.Shovel, 0, SoundMat.Grass, top: "grass_top", bottom: "dirt", drop: B.Dirt, tint: TintKind.Grass);
            Cube(B.Sand, "Sand", "sand", 0.5f, ToolKind.Shovel, 0, SoundMat.Sand);
            Cube(B.Sandstone, "Sandstone", "sandstone_side", 0.8f, ToolKind.Pickaxe, 1, SoundMat.Stone, top: "sandstone_top", bottom: "sandstone_top");
            Cube(B.Gravel, "Gravel", "gravel", 0.6f, ToolKind.Shovel, 0, SoundMat.Gravel);
            Cube(B.Clay, "Clay", "clay", 0.6f, ToolKind.Shovel, 0, SoundMat.Dirt);
            Cube(B.Snow, "Snow", "snow", 0.2f, ToolKind.Shovel, 0, SoundMat.Snow);
            Cube(B.Ice, "Ice", "ice", 0.5f, ToolKind.Pickaxe, 0, SoundMat.Glass, drop: 0);
            Cube(B.Bedrock, "Bedrock", "bedrock", -1f, ToolKind.None, 0, SoundMat.Stone);
            Cube(B.Cobblestone, "Cobblestone", "cobblestone", 2f, ToolKind.Pickaxe, 1, SoundMat.Stone);
            Cube(B.MossyCobblestone, "Mossy Cobblestone", "mossy_cobblestone", 2f, ToolKind.Pickaxe, 1, SoundMat.Stone);
            Cube(B.Planks, "Planks", "planks", 2f, ToolKind.Axe, 0, SoundMat.Wood);
            Cube(B.LogOak, "Oak Log", "log_oak_side", 2f, ToolKind.Axe, 0, SoundMat.Wood, top: "log_oak_top", bottom: "log_oak_top");
            Cube(B.LogBirch, "Birch Log", "log_birch_side", 2f, ToolKind.Axe, 0, SoundMat.Wood, top: "log_birch_top", bottom: "log_birch_top");
            Cube(B.LogSpruce, "Spruce Log", "log_spruce_side", 2f, ToolKind.Axe, 0, SoundMat.Wood, top: "log_spruce_top", bottom: "log_spruce_top");
            Cube(B.LogDark, "Darkwood Log", "log_dark_side", 2f, ToolKind.Axe, 0, SoundMat.Wood, top: "log_dark_top", bottom: "log_dark_top");
            Cube(B.CoalOre, "Coal Ore", "coal_ore", 3f, ToolKind.Pickaxe, 1, SoundMat.Stone, drop: Items.ItemIds.Coal);
            Cube(B.IronOre, "Iron Ore", "iron_ore", 3f, ToolKind.Pickaxe, 2, SoundMat.Stone, drop: Items.ItemIds.RawIron);
            Cube(B.GoldOre, "Gold Ore", "gold_ore", 3f, ToolKind.Pickaxe, 3, SoundMat.Stone, drop: Items.ItemIds.RawGold);
            Cube(B.Cactus, "Cactus", "cactus_side", 0.4f, ToolKind.None, 0, SoundMat.Plant, top: "cactus_top", bottom: "cactus_top");
            Cube(B.Workbench, "Workbench", "workbench_side", 2.5f, ToolKind.Axe, 0, SoundMat.Wood, top: "workbench_top", bottom: "planks");
            Cube(B.Furnace, "Furnace", "furnace_side", 3.5f, ToolKind.Pickaxe, 1, SoundMat.Stone, top: "furnace_top", bottom: "furnace_top", front: "furnace_front");
            Cube(B.Podzol, "Podzol", "podzol_side", 0.5f, ToolKind.Shovel, 0, SoundMat.Dirt, top: "podzol_top", bottom: "dirt", drop: B.Dirt);
            Cube(B.SnowyGrass, "Snowy Grass", "snowy_grass_side", 0.6f, ToolKind.Shovel, 0, SoundMat.Snow, top: "snow", bottom: "dirt", drop: B.Dirt);

            Leaves(B.LeavesOak, "Oak Leaves", "leaves_oak");
            Leaves(B.LeavesBirch, "Birch Leaves", "leaves_birch");
            Leaves(B.LeavesSpruce, "Spruce Leaves", "leaves_spruce");
            Leaves(B.LeavesDark, "Darkwood Leaves", "leaves_dark");

            var glass = Def(B.Glass, "Glass", "glass", RenderShape.Glass, true, false, 0.3f, ToolKind.None, 0, SoundMat.Glass);
            glass.Drop = 0;

            var water = Def(B.Water, "Water", "water_still", RenderShape.Liquid, false, false, -1f, ToolKind.None, 0, SoundMat.Water);
            water.LightAbsorb = 1; water.Replaceable = true; water.Drop = 0;

            Plant(B.Torch, "Torch", "torch", wind: false).LightEmit = 14;
            Defs[B.Torch].Emissive = true;
            Defs[B.Torch].Sound = SoundMat.Wood;
            Plant(B.TallGrass, "Tall Grass", "tall_grass", tint: TintKind.Grass).Drop = 0;
            Plant(B.Fern, "Fern", "fern", tint: TintKind.Grass).Drop = 0;
            Plant(B.FlowerRed, "Emberbloom", "flower_red");
            Plant(B.FlowerYellow, "Sunpetal", "flower_yellow");
            Plant(B.FlowerBlue, "Frostbell", "flower_blue");
            Plant(B.FlowerWhite, "Moonlace", "flower_white");
            Plant(B.DeadBush, "Dry Shrub", "dead_bush");
            Plant(B.MushroomRed, "Red Toadstool", "mushroom_red", wind: false);
            Plant(B.MushroomBrown, "Brown Cap", "mushroom_brown", wind: false);
            var glow = Plant(B.GlowCap, "Glowcap", "glowcap", wind: false);
            glow.LightEmit = 9; glow.Emissive = true;

            var air = new BlockDef { Id = B.Air, Name = "Air", Key = "air", Shape = RenderShape.None, Replaceable = true, Hardness = -1 };
            Defs[B.Air] = air;

            for (int i = 0; i < 256; i++)
            {
                var d = Defs[i];
                if (d == null) continue;
                if (string.IsNullOrEmpty(d.Key)) d.Key = d.Name.ToLowerInvariant().Replace(' ', '_');
                IsOpaque[i] = d.Opaque;
                IsSolid[i] = d.Solid;
                ShapeOf[i] = d.Shape;
                Emit[i] = d.LightEmit;
                Absorb[i] = d.LightAbsorb;
            }
        }

        static BlockDef Def(byte id, string name, string tex, RenderShape shape, bool solid, bool opaque,
            float hardness, ToolKind tool, int tier, SoundMat sound)
        {
            var d = new BlockDef
            {
                Id = id, Name = name, Shape = shape, Solid = solid, Opaque = opaque, Hardness = hardness,
                Tool = tool, MinTier = tier, Sound = sound, Top = tex, Bottom = tex, Side = tex, Front = tex,
                Key = tex,
            };
            Defs[id] = d;
            return d;
        }

        static void Cube(byte id, string name, string side, float hardness, ToolKind tool, int tier, SoundMat sound,
            string top = null, string bottom = null, string front = null, int drop = -1, TintKind tint = TintKind.None)
        {
            var d = Def(id, name, side, RenderShape.Cube, true, true, hardness, tool, tier, sound);
            d.Key = name.ToLowerInvariant().Replace(' ', '_');
            if (top != null) d.Top = top;
            if (bottom != null) d.Bottom = bottom;
            d.Front = front ?? side;
            d.Drop = drop;
            d.Tint = tint;
        }

        static void Leaves(byte id, string name, string tex)
        {
            var d = Def(id, name, tex, RenderShape.Leaves, true, false, 0.2f, ToolKind.None, 0, SoundMat.Leaves);
            d.LightAbsorb = 1; d.Tint = TintKind.Foliage; d.Wind = true; d.Drop = 0;
        }

        static BlockDef Plant(byte id, string name, string tex, TintKind tint = TintKind.None, bool wind = true)
        {
            var d = Def(id, name, tex, RenderShape.Cross, false, false, 0f, ToolKind.None, 0, SoundMat.Plant);
            d.Tint = tint; d.Wind = wind; d.Replaceable = id != B.Torch; d.NeedsSupport = true;
            return d;
        }

        public static BlockDef Get(int id) => Defs[id] ?? Defs[B.Air];
        public static bool IsLog(byte id) => id >= B.LogOak && id <= B.LogDark;
        public static bool IsLeaves(byte id) => id >= B.LeavesOak && id <= B.LeavesDark;
        public static bool IsPlant(byte id) => ShapeOf[id] == RenderShape.Cross;
        public static bool Exists(int id) => id >= 0 && id < 256 && Defs[id] != null;
    }
}
