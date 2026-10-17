using System.IO;
using UnityEditor;
using UnityEngine;

// Batch check for the generated block textures:  tools/unity.sh GraphicsTextureCheck.Run
// Prints one WM_TEX line per texture and exits 0 when every import matches the contract.
public static class GraphicsTextureCheck
{
    [System.Serializable]
    class SheetMeta
    {
        public int tile;
        public int columns;
        public int rows;
        public string[] layers;
    }

    static int fails;

    static void Fail(string msg)
    {
        fails++;
        Debug.Log("WM_FAIL " + msg);
    }

    public static void Run()
    {
        fails = 0;
        try { Check(); }
        catch (System.Exception e) { Fail("exception " + e); }
        Debug.Log(fails == 0 ? "WM_TEX_OK" : "WM_TEX_FAILED count=" + fails);
        EditorApplication.Exit(fails == 0 ? 0 : 1);
    }

    static void Check()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var json = Resources.Load<TextAsset>("Blocks/blocks");
        if (json == null) { Fail("Resources/Blocks/blocks.json missing"); return; }
        var meta = JsonUtility.FromJson<SheetMeta>(json.text);
        int slots = meta.columns * meta.rows;
        int layers = meta.layers != null ? meta.layers.Length : 0;
        Debug.Log($"WM_TEX blocks.json tile={meta.tile} columns={meta.columns} rows={meta.rows} layers={layers} slots={slots}");
        if (layers == 0 || layers > slots || layers <= slots - meta.columns)
            Fail($"layer count {layers} does not fit {meta.columns}x{meta.rows}");

        foreach (var name in new[] { "blocks_albedo", "blocks_normal", "blocks_mask" })
        {
            var arr = Resources.Load<Texture2DArray>("Blocks/" + name);
            if (arr == null) { Fail(name + " is not a Texture2DArray"); continue; }
            var imp = (TextureImporter)AssetImporter.GetAtPath("Assets/Resources/Blocks/" + name + ".png");
            Debug.Log($"WM_TEX {name} depth={arr.depth} size={arr.width}x{arr.height} format={arr.format} " +
                      $"mips={arr.mipmapCount} srgb={imp.sRGBTexture} wrap={arr.wrapMode} filter={arr.filterMode} aniso={arr.anisoLevel} layers={layers}");
            // Unity makes columns*rows slices; the trailing ones are the contract's padding tiles.
            if (arr.depth != slots) Fail($"{name} depth {arr.depth} != columns*rows {slots}");
            if (arr.depth < layers) Fail($"{name} depth {arr.depth} < layer count {layers}");
            if (arr.width != meta.tile || arr.height != meta.tile) Fail($"{name} slice size {arr.width}x{arr.height} != {meta.tile}");
            if (arr.mipmapCount < 2) Fail(name + " has no mipmaps");
            if (imp.sRGBTexture != (name == "blocks_albedo")) Fail(name + " has wrong sRGB flag");
            if (imp.textureType != TextureImporterType.Default) Fail(name + " should import as Default type");
            if (arr.wrapMode != TextureWrapMode.Repeat) Fail(name + " should wrap Repeat");
        }

        var crack = Resources.Load<Texture2DArray>("Blocks/crack");
        if (crack == null) Fail("crack is not a Texture2DArray");
        else
        {
            Debug.Log($"WM_TEX crack depth={crack.depth} size={crack.width}x{crack.height} format={crack.format} mips={crack.mipmapCount}");
            if (crack.depth != 8) Fail("crack depth " + crack.depth + " != 8");
        }

        foreach (var name in new[] { "water_normal_a", "water_normal_b", "water_foam", "cloud_noise" })
        {
            var tex = Resources.Load<Texture2D>("Textures/" + name);
            if (tex == null) { Fail("Textures/" + name + " missing"); continue; }
            var imp = (TextureImporter)AssetImporter.GetAtPath("Assets/Resources/Textures/" + name + ".png");
            Debug.Log($"WM_TEX {name} size={tex.width}x{tex.height} format={tex.format} mips={tex.mipmapCount} wrap={tex.wrapMode} srgb={imp.sRGBTexture}");
            if (tex.wrapMode != TextureWrapMode.Repeat) Fail(name + " should wrap Repeat");
            if (imp.sRGBTexture) Fail(name + " should be linear");
            if (tex.width != 256 || tex.height != 256) Fail(name + " should be 256x256");
        }

        foreach (var name in new[] { "stick", "coal", "raw_iron", "iron_ingot", "raw_gold", "gold_ingot", "flint" })
        {
            var sp = Resources.Load<Sprite>("Textures/Items/" + name);
            if (sp == null) { Fail("item sprite " + name + " missing"); continue; }
            Debug.Log($"WM_TEX item {name} size={sp.texture.width}x{sp.texture.height} format={sp.texture.format} mips={sp.texture.mipmapCount} wrap={sp.texture.wrapMode}");
            if (sp.texture.wrapMode != TextureWrapMode.Clamp) Fail(name + " should clamp");
        }
    }
}
