using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// One-shot, idempotent project configuration (player settings, layers, URP, quality levels,
/// always-included shaders, the bootstrap scene). Batch entry points:
///   tools/unity.sh WildmereSetup.RunBatch     configure everything
///   tools/unity.sh WildmereSetup.CompileCheck  just prove scripts compile (prints WM_COMPILE_OK)
/// </summary>
public static class WildmereSetup
{
    public const string ScenePath = "Assets/Scenes/Main.unity";
    const string kRpAsset = "Assets/Settings/PC_RPAsset.asset";
    const string kRenderer = "Assets/Settings/PC_Renderer.asset";

    static readonly (int index, string name)[] kLayers =
    {
        (6, "World"), (7, "Player"), (8, "Entity"), (9, "Viewmodel"), (10, "Fx"), (11, "Props"),
    };

    static readonly string[] kShaders =
    {
        "Wildmere/Voxel", "Wildmere/VoxelCutout", "Wildmere/Water", "Wildmere/Sky", "Wildmere/Prop",
        "Wildmere/Outline", "Wildmere/Crack",
        "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit",
        "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Particles/Simple Lit",
        "Sprites/Default", "UI/Default",
    };

    public static string Version => File.Exists("VERSION") ? File.ReadAllText("VERSION").Trim() : "0.0.0";

    [MenuItem("Wildmere/Configure Project")]
    public static void Run()
    {
        ConfigurePlayer();
        ConfigureLayers();
        ConfigureUrp();
        ConfigureQuality();
        RegisterShaders();
        WriteVersion();
        CreateScene();
        AssetDatabase.SaveAssets();
        Debug.Log("WM_SETUP_OK version=" + Version);
    }

    public static void RunBatch() { Run(); EditorApplication.Exit(0); }

    public static void CompileCheck()
    {
        bool failed = EditorUtility.scriptCompilationFailed;
        Debug.Log(failed ? "WM_FAIL compile" : "WM_COMPILE_OK");
        EditorApplication.Exit(failed ? 1 : 0);
    }

    /// <summary>Report compile messages for every Wildmere shader (fails on errors).</summary>
    public static void ShaderCheck()
    {
        int errors = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" }))
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
            var msgs = ShaderUtil.GetShaderMessages(sh);
            int e = msgs.Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
            errors += e;
            Debug.Log($"WM_SHADER {sh.name} errors={e} warnings={msgs.Length - e}");
            foreach (var m in msgs) Debug.Log($"WM_SHADER   {m.severity} {m.file}:{m.line} {m.message}");
        }
        EditorApplication.Exit(errors == 0 ? 0 : 1);
    }

    static void ConfigurePlayer()
    {
        PlayerSettings.companyName = "Wildmere";
        PlayerSettings.productName = "Wildmere";
        PlayerSettings.bundleVersion = Version;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.wildmere.game");
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.defaultIsNativeResolution = false;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.macRetinaSupport = true;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Disabled);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.usePlayerLog = true;
        UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.x64ARM64;

        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Icons/AppIcon.png");
        if (icon != null) PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
    }

    static void ConfigureLayers()
    {
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        foreach (var (index, name) in kLayers) layers.GetArrayElementAtIndex(index).stringValue = name;
        tm.ApplyModifiedPropertiesWithoutUndo();
    }

    static void ConfigureUrp()
    {
        var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(kRpAsset);
        if (asset == null) { Debug.LogError("WM_FAIL missing " + kRpAsset); return; }
        var so = new SerializedObject(asset);
        void F(string p, float v) { var s = so.FindProperty(p); if (s != null) s.floatValue = v; }
        void I(string p, int v) { var s = so.FindProperty(p); if (s != null) s.intValue = v; }
        void Bo(string p, bool v) { var s = so.FindProperty(p); if (s != null) s.boolValue = v; }
        void V3(string p, Vector3 v) { var s = so.FindProperty(p); if (s != null) s.vector3Value = v; }

        F("m_ShadowDistance", 96f);
        I("m_ShadowCascadeCount", 4);
        V3("m_Cascade4Split", new Vector3(0.07f, 0.2f, 0.46f));
        F("m_CascadeBorder", 0.15f);
        F("m_ShadowDepthBias", 0.6f);
        F("m_ShadowNormalBias", 0.9f);
        Bo("m_SoftShadowsSupported", true);
        I("m_SoftShadowQuality", 2);
        I("m_MainLightShadowmapResolution", 2048);
        I("m_AdditionalLightsRenderingMode", 0);     // disabled: torches are voxel-lit
        Bo("m_AdditionalLightShadowsSupported", false);
        I("m_MSAA", 2);
        Bo("m_SupportsHDR", true);
        I("m_ColorGradingMode", 1);
        Bo("m_RequireDepthTexture", true);           // water depth/foam, SSAO
        Bo("m_RequireOpaqueTexture", false);
        Bo("m_UseSRPBatcher", true);
        Bo("m_SupportsDynamicBatching", false);
        Bo("m_UseAdaptivePerformance", false);
        F("m_RenderScale", 1f);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);

        GraphicsSettings.defaultRenderPipeline = asset;

        // Make sure the renderer has SSAO (the runtime toggles it per preset).
        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(kRenderer);
        if (rd != null)
        {
            var ssao = rd.rendererFeatures.FirstOrDefault(f => f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion");
            if (ssao == null) Debug.Log("WM_SETUP note: renderer has no SSAO feature");
            else
            {
                var fs = new SerializedObject(ssao);
                var st = fs.FindProperty("m_Settings");
                void S(string n, System.Action<SerializedProperty> a) { var p = st?.FindPropertyRelative(n); if (p != null) a(p); }
                S("Intensity", p => p.floatValue = 0.6f);
                S("Radius", p => p.floatValue = 0.35f);
                S("DirectLightingStrength", p => p.floatValue = 0.1f);
                S("Falloff", p => p.floatValue = 60f);
                S("Downsample", p => p.boolValue = true);
                S("Samples", p => p.intValue = 1);
                fs.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(ssao);
                Debug.Log("WM_SETUP ssao configured");
            }
            EditorUtility.SetDirty(rd);
        }
    }

    static void ConfigureQuality()
    {
        var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(kRpAsset);
        var quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var levels = quality.FindProperty("m_QualitySettings");
        string[] names = { "Low", "Medium", "High", "Ultra" };
        levels.arraySize = 4;
        for (int i = 0; i < 4; i++)
        {
            var q = levels.GetArrayElementAtIndex(i);
            q.FindPropertyRelative("name").stringValue = names[i];
            q.FindPropertyRelative("customRenderPipeline").objectReferenceValue = asset;
            q.FindPropertyRelative("excludedTargetPlatforms").arraySize = 0;
            q.FindPropertyRelative("vSyncCount").intValue = 1;
            q.FindPropertyRelative("globalTextureMipmapLimit").intValue = 0;
            q.FindPropertyRelative("anisotropicTextures").intValue = 2;
            q.FindPropertyRelative("lodBias").floatValue = 1f;
            q.FindPropertyRelative("realtimeReflectionProbes").boolValue = false;
            q.FindPropertyRelative("softParticles").boolValue = false;
            q.FindPropertyRelative("asyncUploadTimeSlice").intValue = 2;
        }
        quality.FindProperty("m_CurrentQuality").intValue = 2;
        var platforms = quality.FindProperty("m_PerPlatformDefaultQuality");
        if (platforms != null)
            for (int i = 0; i < platforms.arraySize; i++)
            {
                var e = platforms.GetArrayElementAtIndex(i);
                var second = e.FindPropertyRelative("second");
                if (second != null) second.intValue = 2;
            }
        quality.ApplyModifiedPropertiesWithoutUndo();
    }

    static void RegisterShaders()
    {
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
        var so = new SerializedObject(gs);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        var have = new System.Collections.Generic.HashSet<Object>();
        for (int i = 0; i < arr.arraySize; i++) { var o = arr.GetArrayElementAtIndex(i).objectReferenceValue; if (o != null) have.Add(o); }
        foreach (var n in kShaders)
        {
            var sh = Shader.Find(n);
            if (sh == null) { Debug.LogError("WM_FAIL shader missing: " + n); continue; }
            if (have.Contains(sh)) continue;
            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            have.Add(sh);
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log("WM_SETUP always-included shaders: " + arr.arraySize);
    }

    static void WriteVersion()
    {
        Directory.CreateDirectory("Assets/Resources");
        string p = "Assets/Resources/version.txt";
        if (!File.Exists(p) || File.ReadAllText(p).Trim() != Version)
        {
            File.WriteAllText(p, Version + "\n");
            AssetDatabase.ImportAsset(p);
        }
    }

    static void CreateScene()
    {
        if (!File.Exists(ScenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("GameRoot").AddComponent<Wildmere.Core.GameRoot>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("WM_SETUP scene written " + ScenePath);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }
}
