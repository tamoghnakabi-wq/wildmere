using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Player builds (batch): tools/unity.sh BuildScript.BuildMac | BuildScript.BuildWindows | BuildScript.BuildAll
/// macOS: Build/mac/Wildmere.app (Universal arm64 + x86_64, ad-hoc signed, not notarized).
/// Windows: Build/win/Wildmere.exe + Wildmere_Data, UnityPlayer.dll, MonoBleedingEdge (ship the folder).
/// </summary>
public static class BuildScript
{
    public static void BuildMac() => Exit(Build(BuildTarget.StandaloneOSX, "Build/mac/Wildmere.app"));
    public static void BuildWindows() => Exit(Build(BuildTarget.StandaloneWindows64, "Build/win/Wildmere.exe"));

    public static void BuildAll()
    {
        bool ok = Build(BuildTarget.StandaloneOSX, "Build/mac/Wildmere.app");
        ok &= Build(BuildTarget.StandaloneWindows64, "Build/win/Wildmere.exe");
        Exit(ok);
    }

    static void Exit(bool ok) { if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1); }

    static bool Build(BuildTarget target, string path)
    {
        WildmereSetup.Run();
        string dir = target == BuildTarget.StandaloneOSX ? path : Path.GetDirectoryName(path);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { WildmereSetup.ScenePath },
            locationPathName = path,
            target = target,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.CompressWithLz4HC,
        };
        var sw = Stopwatch.StartNew();
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"BUILD_RESULT target={target} result={s.result} time={sw.Elapsed.TotalSeconds:F0}s size={s.totalSize / 1048576f:F1}MB errors={s.totalErrors} warnings={s.totalWarnings}");
        if (s.result != BuildResult.Succeeded)
        {
            foreach (var step in report.steps)
                foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    Debug.LogError($"BUILD_ERR [{step.name}] {m.content}");
            return false;
        }

        if (target == BuildTarget.StandaloneOSX)
        {
            string full = Path.GetFullPath(path);
            Run("/usr/bin/xattr", $"-cr \"{full}\"");
            Run("/usr/bin/codesign", $"--force --deep --sign - \"{full}\"");
        }
        else
        {
            foreach (var need in new[] { "Wildmere.exe", "UnityPlayer.dll", "Wildmere_Data" })
                if (!File.Exists(Path.Combine(dir, need)) && !Directory.Exists(Path.Combine(dir, need)))
                { Debug.LogError($"BUILD_ERR windows build missing {need}"); return false; }
            // Debug symbols are not shipped.
            foreach (var d in Directory.GetDirectories(dir, "*_BurstDebugInformation_DoNotShip")) Directory.Delete(d, true);
        }
        Debug.Log($"BUILD_OK {target} {path}");
        return true;
    }

    static void Run(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            p.WaitForExit(120000);
            if (p.ExitCode != 0) Debug.LogWarning($"post-build {exe} exit {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        }
        catch (Exception e) { Debug.LogWarning($"post-build step failed ({exe}): {e.Message}"); }
    }
}
