using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;

namespace Wildmere.World
{
    /// <summary>
    /// Loads the block texture arrays produced by art-source/textures (Resources/Blocks) and resolves
    /// every block face to a layer index. If the sheets are missing (early development) it builds a
    /// small flat-colour fallback array so the world still renders.
    /// </summary>
    public static class BlockTextures
    {
        public static Texture2DArray Albedo, Normal, Mask, Crack;
        public static bool Fallback { get; private set; }
        public static readonly Dictionary<string, int> Layers = new Dictionary<string, int>();
        public static readonly HashSet<string> Emissive = new HashSet<string>();
        static bool sLoaded;

        // Average colours for break particles and the fallback array.
        static readonly Dictionary<string, Color32> kColours = new Dictionary<string, Color32>
        {
            {"grass_top", new Color32(150,150,150,255)}, {"grass_side", new Color32(122,92,62,255)}, {"dirt", new Color32(120,88,60,255)},
            {"snowy_grass_side", new Color32(150,130,115,255)}, {"podzol_top", new Color32(92,64,40,255)}, {"podzol_side", new Color32(110,80,55,255)},
            {"stone", new Color32(124,124,122,255)}, {"cobblestone", new Color32(112,112,110,255)}, {"mossy_cobblestone", new Color32(98,112,86,255)},
            {"bedrock", new Color32(60,60,60,255)}, {"sand", new Color32(214,196,148,255)}, {"sandstone_side", new Color32(206,186,138,255)},
            {"sandstone_top", new Color32(214,196,150,255)}, {"gravel", new Color32(128,120,114,255)}, {"clay", new Color32(156,162,174,255)},
            {"snow", new Color32(236,242,248,255)}, {"ice", new Color32(160,196,236,255)}, {"water_still", new Color32(48,92,150,255)},
            {"log_oak_side", new Color32(102,78,50,255)}, {"log_oak_top", new Color32(160,128,82,255)}, {"log_birch_side", new Color32(214,210,198,255)},
            {"log_birch_top", new Color32(186,166,120,255)}, {"log_spruce_side", new Color32(78,56,36,255)}, {"log_spruce_top", new Color32(140,108,70,255)},
            {"log_dark_side", new Color32(58,44,30,255)}, {"log_dark_top", new Color32(112,86,56,255)}, {"planks", new Color32(170,134,86,255)},
            {"glass", new Color32(200,220,230,90)}, {"leaves_oak", new Color32(130,130,130,255)}, {"leaves_birch", new Color32(140,140,140,255)},
            {"leaves_spruce", new Color32(110,110,110,255)}, {"leaves_dark", new Color32(100,100,100,255)}, {"coal_ore", new Color32(104,104,104,255)},
            {"iron_ore", new Color32(136,126,116,255)}, {"gold_ore", new Color32(142,134,104,255)}, {"cactus_side", new Color32(70,120,56,255)},
            {"cactus_top", new Color32(90,140,70,255)}, {"workbench_top", new Color32(150,112,70,255)}, {"workbench_side", new Color32(140,104,66,255)},
            {"furnace_front", new Color32(100,100,98,255)}, {"furnace_side", new Color32(112,112,110,255)}, {"furnace_top", new Color32(118,118,116,255)},
            {"torch", new Color32(250,190,90,255)}, {"tall_grass", new Color32(140,140,140,255)}, {"fern", new Color32(130,130,130,255)},
            {"flower_red", new Color32(200,60,50,255)}, {"flower_yellow", new Color32(230,200,60,255)}, {"flower_blue", new Color32(80,120,220,255)},
            {"flower_white", new Color32(236,236,230,255)}, {"dead_bush", new Color32(140,104,64,255)}, {"mushroom_red", new Color32(190,50,40,255)},
            {"mushroom_brown", new Color32(150,110,80,255)}, {"glowcap", new Color32(110,230,210,255)},
        };

        public static Color32 ParticleColour(string layer) =>
            layer != null && kColours.TryGetValue(layer, out var c) ? c : new Color32(128, 128, 128, 255);

        public static void Load()
        {
            if (sLoaded) return;
            sLoaded = true;

            var manifest = Resources.Load<TextAsset>("Blocks/blocks");
            Albedo = Resources.Load<Texture2DArray>("Blocks/blocks_albedo");
            Normal = Resources.Load<Texture2DArray>("Blocks/blocks_normal");
            Mask = Resources.Load<Texture2DArray>("Blocks/blocks_mask");
            Crack = Resources.Load<Texture2DArray>("Blocks/crack");

            if (manifest != null && Albedo != null && Normal != null && Mask != null)
            {
                var root = Json.Parse(manifest.text);
                var layers = Json.Arr(root, "layers");
                for (int i = 0; i < layers.Count; i++) Layers[(string)layers[i]] = i;
                var flags = Json.Obj(root, "flags");
                if (flags != null)
                    foreach (var kv in flags)
                        if (kv.Value is List<object> l && l.Contains("emissive")) Emissive.Add(kv.Key);
                Debug.Log($"WM_TEXTURES layers={Layers.Count} albedo={Albedo.width}x{Albedo.height}x{Albedo.depth}");
            }
            else
            {
                BuildFallback();
            }
            if (Crack == null) Crack = BuildFallbackCrack();

            for (int id = 0; id < 256; id++)
            {
                var d = Blocks.Defs[id];
                if (d == null) continue;
                d.LTop = Resolve(d.Top);
                d.LBottom = Resolve(d.Bottom);
                d.LSide = Resolve(d.Side);
                d.LFront = Resolve(d.Front);
                d.Particle = ParticleColour(d.Side);
            }
        }

        static int Resolve(string name)
        {
            if (name != null && Layers.TryGetValue(name, out int i)) return i;
            if (name != null) Debug.LogWarning($"WM_TEXTURES missing layer '{name}'");
            return 0;
        }

        static void BuildFallback()
        {
            Fallback = true;
            const int N = 32;
            var names = new List<string>(kColours.Keys);
            Albedo = new Texture2DArray(N, N, names.Count, TextureFormat.RGBA32, true, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            Normal = new Texture2DArray(N, N, names.Count, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
            Mask = new Texture2DArray(N, N, names.Count, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[N * N];
            var nrm = new Color32[N * N];
            var msk = new Color32[N * N];
            for (int i = 0; i < N * N; i++) nrm[i] = new Color32(128, 128, 255, 255);
            for (int l = 0; l < names.Count; l++)
            {
                var c = kColours[names[l]];
                bool tint = names[l].StartsWith("leaves") || names[l] == "grass_top" || names[l] == "tall_grass" || names[l] == "fern";
                bool cross = names[l] == "tall_grass" || names[l] == "fern" || names[l].StartsWith("flower") || names[l].StartsWith("mushroom")
                             || names[l] == "dead_bush" || names[l] == "torch" || names[l] == "glowcap";
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        uint h = (uint)(x * 73856093 ^ y * 19349663 ^ l * 83492791);
                        float j = 0.85f + (h % 1000) / 1000f * 0.3f;
                        byte a = 255;
                        if (cross) a = (byte)(Mathf.Abs(x - N / 2) < 3 + (N - y) / 6 && y < N - 4 ? 255 : 0);
                        if (names[l].StartsWith("leaves") && h % 7 == 0) a = 0;
                        if (names[l] == "glass") a = (byte)(x < 2 || y < 2 || x > N - 3 || y > N - 3 ? 255 : 30);
                        px[x + y * N] = new Color32((byte)Mathf.Min(255, c.r * j), (byte)Mathf.Min(255, c.g * j), (byte)Mathf.Min(255, c.b * j), a);
                        msk[x + y * N] = new Color32(255, 60, 128, (byte)(tint ? 255 : 0));
                    }
                if (names[l] == "grass_side")
                    for (int y = N - 6; y < N; y++)
                        for (int x = 0; x < N; x++) { px[x + y * N] = new Color32(150, 150, 150, 255); msk[x + y * N].a = 255; }
                Albedo.SetPixels32(px, l);
                Normal.SetPixels32(nrm, l);
                Mask.SetPixels32(msk, l);
                Layers[names[l]] = l;
            }
            Emissive.Add("torch"); Emissive.Add("glowcap");
            Albedo.Apply(true, true); Normal.Apply(true, true); Mask.Apply(true, true);
            Debug.Log($"WM_TEXTURES fallback layers={names.Count}");
        }

        static Texture2DArray BuildFallbackCrack()
        {
            const int N = 32;
            var t = new Texture2DArray(N, N, 8, TextureFormat.RGBA32, true, false) { filterMode = FilterMode.Point };
            var px = new Color32[N * N];
            for (int s = 0; s < 8; s++)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    uint h = (uint)(i * 2654435761u ^ 0x5bd1e995u);
                    px[i] = (h % 100) < (s + 1) * 6 ? new Color32(20, 20, 20, 200) : new Color32(0, 0, 0, 0);
                }
                t.SetPixels32(px, s);
            }
            t.Apply(true, true);
            return t;
        }
    }
}
