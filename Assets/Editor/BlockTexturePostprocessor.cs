using System.IO;
using UnityEditor;
using UnityEngine;

// Import rules for Wildmere's generated textures (art-source/textures/generate.py).
//
//   Assets/Resources/Blocks/blocks_albedo.png  -> Texture2DArray, sRGB, alpha = cutout coverage
//   Assets/Resources/Blocks/blocks_normal.png  -> Texture2DArray, linear. Imported as a *Default*
//        texture with sRGB off, NOT as TextureImporterType.NormalMap: the voxel shader unpacks the
//        tangent-space normal itself from RG (OpenGL +Y, z reconstructed), so Unity must not
//        swizzle/re-encode it. Compressed to BC5 (two channels) on desktop for best quality.
//   Assets/Resources/Blocks/blocks_mask.png    -> Texture2DArray, linear RGBA (AO, smoothness, height, tint/emission)
//   Assets/Resources/Blocks/crack.png          -> Texture2DArray (8 x 1 break stages), linear
//   Assets/Resources/Textures/*.png            -> 2D repeat textures (water normals, foam, cloud noise), linear
//   Assets/Resources/Textures/Items/*.png      -> single UI sprites, clamp, no mips
//
// Flipbook columns/rows for the block sheets come from blocks.json next to them; Unity slices
// flipbooks left-to-right, top-to-bottom, which matches the generator's row-major layout, so the
// slice index equals the position in blocks.json "layers" (trailing slices are padding).
public class BlockTexturePostprocessor : AssetPostprocessor
{
    const string BlocksDir = "Assets/Resources/Blocks/";
    const string TexturesDir = "Assets/Resources/Textures/";
    const string ItemsDir = "Assets/Resources/Textures/Items/";
    const string BlocksJson = BlocksDir + "blocks.json";

    [System.Serializable]
    class SheetMeta
    {
        public int tile = 128;
        public int columns = 8;
        public int rows = 0;
    }

    // Bump when these rules change so Unity reimports the affected textures.
    public override uint GetVersion() { return 3; }

    void OnPreprocessTexture()
    {
        string path = assetPath.Replace('\\', '/');
        var ti = (TextureImporter)assetImporter;
        string file = Path.GetFileNameWithoutExtension(path);
        string dir = Path.GetDirectoryName(path).Replace('\\', '/') + "/";

        if (dir == BlocksDir)
        {
            if (file == "crack")
                ImportArray(ti, path, 8, 1, srgb: false, kind: "crack");
            else if (file.StartsWith("blocks_"))
            {
                context.DependsOnSourceAsset(BlocksJson);
                int cols, rows;
                ReadGrid(path, out cols, out rows);
                ImportArray(ti, path, cols, rows, srgb: file == "blocks_albedo", kind: file);
            }
        }
        else if (dir == ItemsDir)
        {
            ImportItemSprite(ti);
        }
        else if (dir == TexturesDir)
        {
            Import2DRepeat(ti);
        }
    }

    static void ImportArray(TextureImporter ti, string path, int cols, int rows, bool srgb, string kind)
    {
        var s = new TextureImporterSettings();
        ti.ReadTextureSettings(s);
        s.textureType = TextureImporterType.Default;
        s.textureShape = TextureImporterShape.Texture2DArray;
        s.flipbookColumns = cols;
        s.flipbookRows = rows;
        ti.SetTextureSettings(s);

        bool albedo = kind == "blocks_albedo";
        bool normal = kind == "blocks_normal";
        ti.textureType = TextureImporterType.Default;
        ti.textureShape = TextureImporterShape.Texture2DArray;
        ti.sRGBTexture = srgb;
        ti.alphaSource = normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;          // the generator already bleeds colour into clear texels
        ti.npotScale = TextureImporterNPOTScale.None; // the sheet height (rows * 128) need not be a power of two
        ti.isReadable = false;
        ti.mipmapEnabled = true;
        ti.mipmapFilter = albedo ? TextureImporterMipFilter.KaiserFilter : TextureImporterMipFilter.BoxFilter;
        ti.mipMapsPreserveCoverage = albedo;     // keeps leaf/plant cutouts from thinning out at distance
        ti.alphaTestReferenceValue = 0.5f;
        ti.streamingMipmaps = false;
        ti.filterMode = FilterMode.Trilinear;
        ti.anisoLevel = 8;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.maxTextureSize = 8192;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;

        var standalone = ti.GetPlatformTextureSettings("Standalone");
        standalone.overridden = normal;
        standalone.maxTextureSize = 8192;
        if (normal)
        {
            standalone.format = TextureImporterFormat.BC5; // RG only: matches the shader's RG decode
            standalone.textureCompression = TextureImporterCompression.CompressedHQ;
        }
        ti.SetPlatformTextureSettings(standalone);
    }

    static void Import2DRepeat(TextureImporter ti)
    {
        ti.textureType = TextureImporterType.Default;
        ti.textureShape = TextureImporterShape.Texture2D;
        ti.sRGBTexture = false;                  // normals, foam mask and cloud noise are data
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;
        ti.mipmapEnabled = true;
        ti.mipmapFilter = TextureImporterMipFilter.BoxFilter;
        ti.filterMode = FilterMode.Trilinear;
        ti.anisoLevel = 4;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.maxTextureSize = 2048;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
    }

    static void ImportItemSprite(TextureImporter ti)
    {
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.spritePixelsPerUnit = 128;
        ti.sRGBTexture = true;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = true;
        ti.mipmapEnabled = false;
        ti.filterMode = FilterMode.Bilinear;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.maxTextureSize = 256;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
    }

    // Columns/rows from blocks.json; falls back to the PNG header (128 px tiles, 8 columns).
    static void ReadGrid(string pngPath, out int cols, out int rows)
    {
        cols = 8;
        rows = 0;
        int tile = 128;
        try
        {
            if (File.Exists(BlocksJson))
            {
                var meta = JsonUtility.FromJson<SheetMeta>(File.ReadAllText(BlocksJson));
                if (meta != null && meta.columns > 0 && meta.rows > 0)
                {
                    cols = meta.columns;
                    rows = meta.rows;
                    tile = meta.tile > 0 ? meta.tile : 128;
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("BlockTexturePostprocessor: could not parse " + BlocksJson + ": " + e.Message);
        }
        int w, h;
        if (ReadPngSize(pngPath, out w, out h))
        {
            if (rows <= 0) { cols = Mathf.Max(1, w / tile); rows = Mathf.Max(1, h / tile); }
            if (w != cols * tile || h != rows * tile)
                Debug.LogWarning($"BlockTexturePostprocessor: {pngPath} is {w}x{h}, expected {cols * tile}x{rows * tile} from blocks.json");
        }
        if (rows <= 0) rows = 1;
    }

    static bool ReadPngSize(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[24];
                if (fs.Read(b, 0, 24) != 24) return false;
                w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return w > 0 && h > 0;
            }
        }
        catch { return false; }
    }
}
