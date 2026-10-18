using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Graphics module build hook: makes sure the runtime-only shaders the graphics code finds with
/// Shader.Find (weather particles, volumetric light) are in GraphicsSettings' always-included list before
/// shaders are gathered. Idempotent; complements WildmereSetup.RegisterShaders (which owns the core list).
/// Also: tools/unity.sh GraphicsBuildShaders.Check prints whether every graphics shader compiles.
/// </summary>
public sealed class GraphicsBuildShaders : IPreprocessBuildWithReport
{
    public static readonly string[] Shaders = { "Wildmere/WeatherFx", "Hidden/Wildmere/Volumetric" };

    public int callbackOrder => 10;

    public void OnPreprocessBuild(BuildReport report) => Register();

    [MenuItem("Wildmere/Graphics/Register Graphics Shaders")]
    public static void Register()
    {
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
        var so = new SerializedObject(gs);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        var have = new HashSet<Object>();
        for (int i = 0; i < arr.arraySize; i++) { var o = arr.GetArrayElementAtIndex(i).objectReferenceValue; if (o != null) have.Add(o); }
        int added = 0;
        foreach (var n in Shaders)
        {
            var sh = Shader.Find(n);
            if (sh == null) { Debug.LogError("WM_FAIL graphics shader missing: " + n); continue; }
            if (have.Contains(sh)) continue;
            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            have.Add(sh);
            added++;
        }
        if (added > 0) so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"WM_GFX_SETUP graphics shaders registered (+{added}, total {arr.arraySize})");
    }
}
