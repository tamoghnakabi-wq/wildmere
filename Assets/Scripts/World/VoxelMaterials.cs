using UnityEngine;

namespace Wildmere.World
{
    /// <summary>Physics/render layers used by runtime code (configured by WildmereSetup).</summary>
    public static class Layers
    {
        public const int World = 6, Player = 7, Entity = 8, Viewmodel = 9, Fx = 10, Props = 11;
    }

    /// <summary>
    /// The shared voxel materials. Shaders are owned by the Graphics module; names are a contract:
    /// "Wildmere/Voxel" (opaque), "Wildmere/VoxelCutout" (alpha-tested leaves/plants/glass),
    /// "Wildmere/Water" (transparent liquid).
    /// </summary>
    public static class VoxelMaterials
    {
        public static Material Opaque, Cutout, Water;
        public static Texture2D GrassTint, FoliageTint;
        public const int TintSize = 1024;
        static bool sDirtyTint;

        static readonly int kAlbedo = Shader.PropertyToID("_BlockAlbedo");
        static readonly int kNormal = Shader.PropertyToID("_BlockNormal");
        static readonly int kMask = Shader.PropertyToID("_BlockMask");
        static readonly int kGrass = Shader.PropertyToID("_WM_GrassTint");
        static readonly int kFoliage = Shader.PropertyToID("_WM_FoliageTint");
        static readonly int kTintSize = Shader.PropertyToID("_WM_TintSize");

        public static void Init()
        {
            if (Opaque != null) return;
            BlockTextures.Load();
            Opaque = Make("Wildmere/Voxel", "Voxel Opaque");
            Cutout = Make("Wildmere/VoxelCutout", "Voxel Cutout");
            Water = Make("Wildmere/Water", "Voxel Water");
            var na = Resources.Load<Texture2D>("Textures/water_normal_a");
            var nb = Resources.Load<Texture2D>("Textures/water_normal_b");
            var foam = Resources.Load<Texture2D>("Textures/water_foam");
            if (na != null) Water.SetTexture("_WaterNormalA", na);
            if (nb != null) Water.SetTexture("_WaterNormalB", nb);
            if (foam != null) Water.SetTexture("_WaterFoam", foam);

            GrassTint = NewTint("GrassTint", new Color32(110, 160, 70, 255));
            FoliageTint = NewTint("FoliageTint", new Color32(90, 140, 60, 255));
            Shader.SetGlobalTexture(kAlbedo, BlockTextures.Albedo);
            Shader.SetGlobalTexture(kNormal, BlockTextures.Normal);
            Shader.SetGlobalTexture(kMask, BlockTextures.Mask);
            Shader.SetGlobalTexture(kGrass, GrassTint);
            Shader.SetGlobalTexture(kFoliage, FoliageTint);
            Shader.SetGlobalFloat(kTintSize, TintSize);
        }

        static Material Make(string shader, string name)
        {
            var sh = Shader.Find(shader);
            if (sh == null)
            {
                Debug.LogError($"WM_FAIL shader not found: {shader}");
                sh = Shader.Find("Universal Render Pipeline/Unlit");
            }
            var m = new Material(sh) { name = name, enableInstancing = false };
            m.SetTexture(kAlbedo, BlockTextures.Albedo);
            m.SetTexture(kNormal, BlockTextures.Normal);
            m.SetTexture(kMask, BlockTextures.Mask);
            return m;
        }

        static Texture2D NewTint(string name, Color32 fill)
        {
            var t = new Texture2D(TintSize, TintSize, TextureFormat.RGBA32, false, false)
            {
                name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[TintSize * TintSize];
            for (int i = 0; i < px.Length; i++) px[i] = fill;
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        /// <summary>Write a generated column's tints into the toroidal tint maps (main thread).</summary>
        public static void WriteTints(Chunk c)
        {
            int x = ((c.CX * 16) % TintSize + TintSize) % TintSize;
            int z = ((c.CZ * 16) % TintSize + TintSize) % TintSize;
            // Chunk tint arrays are x + z*16, which matches SetPixels32 row order (rows = z).
            GrassTint.SetPixels32(x, z, 16, 16, c.GrassTint);
            FoliageTint.SetPixels32(x, z, 16, 16, c.FoliageTint);
            sDirtyTint = true;
        }

        /// <summary>Upload pending tint edits once per frame.</summary>
        public static void FlushTints()
        {
            if (!sDirtyTint) return;
            sDirtyTint = false;
            GrassTint.Apply(false);
            FoliageTint.Apply(false);
        }
    }
}
