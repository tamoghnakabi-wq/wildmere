using UnityEditor;
using UnityEngine;

/// <summary>
/// Import settings for the Blender-built models under Assets/Resources/Models (see BLENDER_ASSETS.md).
/// The FBX files already carry Unity space (+Y up, +Z forward, metres, rotation baked), vertex-colour
/// albedo and one material per surface named M_&lt;Surface&gt;.  Materials are imported (embedded, never
/// extracted) only so every renderer keeps the exact M_&lt;Surface&gt; name: the runtime swaps them for its
/// own shaders by name.
/// </summary>
public class ModelPostprocessor : AssetPostprocessor
{
    public const string ModelRoot = "Assets/Resources/Models/";
    public const string IconRoot = "Assets/Resources/Models/Icons/";

    // Bump when the settings below change so Unity re-imports every model.
    public override uint GetVersion() { return 4; }

    public static bool IsModel(string path)
    {
        return path.StartsWith(ModelRoot) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
    }

    void OnPreprocessModel()
    {
        if (!IsModel(assetPath)) return;
        var mi = (ModelImporter)assetImporter;
        Apply(mi);
    }

    /// <summary>The single source of truth for model import settings (also used by ModelImportCheck).</summary>
    public static void Apply(ModelImporter mi)
    {
        mi.globalScale = 1f;
        mi.useFileScale = true;
        mi.bakeAxisConversion = false;          // axis conversion is baked by the Blender exporter
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        mi.materialSearch = ModelImporterMaterialSearch.Local;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importVisibility = false;
        mi.importBlendShapes = false;
        mi.importConstraints = false;
        mi.importAnimation = false;
        mi.animationType = ModelImporterAnimationType.None;
        mi.addCollider = false;
        mi.isReadable = false;
        mi.importNormals = ModelImporterNormals.Import;
        mi.importTangents = ModelImporterTangents.CalculateMikk;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.meshOptimizationFlags = MeshOptimizationFlags.Everything;
        mi.weldVertices = true;
        mi.preserveHierarchy = false;           // the FBX root node becomes the prefab root
        mi.sortHierarchyByName = false;         // keep authoring order (Body, Neck, Head, ...)
        mi.generateSecondaryUV = false;
        mi.keepQuads = false;
        // Vertex colours are always imported by the FBX importer; nothing to toggle.
    }

    /// <summary>True when an importer already matches Apply() (used by the batch check).</summary>
    public static bool Matches(ModelImporter mi)
    {
        return Mathf.Approximately(mi.globalScale, 1f) && mi.useFileScale && !mi.bakeAxisConversion
            && mi.materialImportMode == ModelImporterMaterialImportMode.ImportViaMaterialDescription
            && mi.materialLocation == ModelImporterMaterialLocation.InPrefab
            && !mi.importCameras && !mi.importLights && !mi.importAnimation
            && mi.animationType == ModelImporterAnimationType.None && !mi.addCollider && !mi.isReadable
            && mi.importNormals == ModelImporterNormals.Import
            && mi.meshCompression == ModelImporterMeshCompression.Off && !mi.preserveHierarchy
            && !mi.sortHierarchyByName;
    }

    void OnPostprocessModel(GameObject root)
    {
        if (!IsModel(assetPath)) return;
        // No colliders, animators or cameras should ever come in; strip defensively.
        foreach (var c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var a in root.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(a);
    }

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(IconRoot)) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.sRGBTexture = true;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = true;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.filterMode = FilterMode.Bilinear;
        ti.maxTextureSize = 256;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.isReadable = false;
    }
}
