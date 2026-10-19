using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Wildmere.Core
{
    /// <summary>
    /// In-game auto-update. Checks a signed manifest attached to the latest GitHub release, downloads the
    /// zip for this platform in the background, verifies size + SHA-256 (from the signed manifest), stages
    /// the new app next to the save data and, when the player restarts or quits, a small helper script
    /// waits for the game to exit, swaps the installed app for the new one (keeping a backup until the
    /// swap succeeds) and relaunches it.
    ///
    /// Security: the manifest is accepted only if its RSA-SHA256 signature verifies with the public key
    /// below (private key: .secrets/wildmere-update-key.pem, never shipped); only newer versions install.
    /// </summary>
    public sealed class Updater : MonoBehaviour
    {
        public enum Phase { Idle, Checking, UpToDate, Available, Downloading, Ready, Error, Unsupported }

        public static Updater Instance { get; private set; }

        public const string FeedUrl = "https://github.com/tamoghnakabi-wq/wildmere/releases/latest/download/wildmere-update.json";
        // RSA-3072 public key modulus (exponent 65537); matches tools/release/update-public.pem.
        const string kModulus = "mmU/CDHxy7lDI2hv1GYTmT6w9jxyY3lBlSjx98YS9d9Sv87RPi9e5jhKaBNWaSFc8cXN8aIZvJkkDoeEefGgZ8BrDoZdmSIfQi5KG6keiVFKsLjTCP2CKVUgLJ4xBgzrc9U0qTmDbyeseMz8np2YeS4wrha6OO9IhM/sbWbEvtYycGuc7myIk2YWU5pSz5bAihfa8Gu2kLs+ZcxcZAbfMHEzo6JKPn/1rIY06Z5a5+SzbHRCj7EKekgiiCv0Uxqw/LQztyYW1bdTHUtg76U5nZTHB6Zi9cpUDtSRe+axTMMXEN+qv9M63rjHxO7pMJ97mIfpiRNmOIq644RDunAvBqohxFIsN1/uhdX3iscNcF9VnGoGqqSD42LwBRrCTygm7D0ZhGh8JfKYMFqnOQ3ew9JkDYXPHHmP6hu6zRGWoKD8K9PA2DcHj3of1BJ5tICXzw1feLie1yevmVk5arOgyHOlBu1quK6iV5xfTVnMu5uP15xHmqQhSAIoYuIB8qGb";

        public Phase State { get; private set; } = Phase.Idle;
        public string Latest { get; private set; }
        public string Summary { get; private set; }
        public string Message { get; private set; }      // user-facing status / error / unsupported reason
        public float Progress { get; private set; }      // 0..1 while downloading
        public long DownloadSize { get; private set; }
        /// <summary>Set after a successful update was installed (shown once on the title screen).</summary>
        public string JustUpdatedFrom { get; private set; }
        public event Action Changed;

        string mFeed;
        string mAssetUrl, mAssetSha, mAssetFile;
        long mAssetSize;
        string mStagedApp;          // extracted new app (mac: Wildmere.app, win: folder with Wildmere.exe)
        float mNextCheck;
        int mFailures;
        bool mTestMode;
        UnityWebRequest mRequest;

        static string Root => Path.Combine(Application.persistentDataPath, "Updates");
        static string PendingPath => Path.Combine(Root, "pending.json");
        static string HelperLog => Path.Combine(Root, "install.log");

        public static string PlatformKey =>
            Application.platform == RuntimePlatform.OSXPlayer ? "macos" :
            Application.platform == RuntimePlatform.WindowsPlayer ? "windows" : null;

        void Awake()
        {
            Instance = this;
            mFeed = Cli.Str("updatefeed", FeedUrl);
            mTestMode = Cli.Has("updatetest");
            FinishPendingInstall();
            string why = UnsupportedReason();
            if (why != null) { State = Phase.Unsupported; Message = why; }
            bool harness = Harness.WantsAutoStart && !mTestMode;
            mNextCheck = Time.realtimeSinceStartup + (mTestMode ? 1f : 4f);
            if (harness || Cli.Has("noupdate")) mNextCheck = float.MaxValue;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Notify() { try { Changed?.Invoke(); } catch (Exception e) { Debug.LogException(e); } }

        void Update()
        {
            if (Time.realtimeSinceStartup < mNextCheck) return;
            if (State == Phase.Checking || State == Phase.Downloading || State == Phase.Ready || State == Phase.Unsupported)
            { mNextCheck = Time.realtimeSinceStartup + 60f; return; }
            mNextCheck = Time.realtimeSinceStartup + 6 * 3600f;
            StartCoroutine(CheckRoutine(false));
        }

        /// <summary>Check now (settings button). Downloads automatically if the setting allows it.</summary>
        public void CheckNow()
        {
            if (State == Phase.Checking || State == Phase.Downloading || State == Phase.Ready || State == Phase.Unsupported) return;
            StartCoroutine(CheckRoutine(true));
        }

        /// <summary>Start downloading an available update (when auto-download is off).</summary>
        public void Download()
        {
            if (State == Phase.Available) StartCoroutine(DownloadRoutine());
        }

        // ------------------------------------------------------------------ platform / install location

        /// <summary>The installed app this process runs from (mac: the .app bundle, win: the folder with the exe).</summary>
        public static string InstallPath
        {
            get
            {
                var data = Path.GetFullPath(Application.dataPath);
                return Path.GetDirectoryName(data.TrimEnd('/', '\\')); // mac: <app>/Contents -> <app>; win: <dir>/Wildmere_Data -> <dir>
            }
        }

        static string UnsupportedReason()
        {
            if (Application.isEditor) return "Updates are disabled in the editor.";
            if (PlatformKey == null) return "Automatic updates are not available on this platform.";
            string app = InstallPath;
            if (app.Contains("/AppTranslocation/"))
                return "Move Wildmere to your Applications folder (or another folder you own) to get automatic updates.";
            try
            {
                string parent = Path.GetDirectoryName(app);
                string probe = Path.Combine(parent, ".wildmere-write-test-" + Process.GetCurrentProcess().Id);
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch
            {
                return "Wildmere can't update itself in this folder. Move it to a folder you own (e.g. Applications or Documents).";
            }
            return null;
        }

        // ------------------------------------------------------------------ check

        IEnumerator CheckRoutine(bool userInitiated)
        {
            State = Phase.Checking; Message = null; Notify();
            byte[] json = null, sig = null;
            yield return Fetch(mFeed, b => json = b);
            if (json != null) yield return Fetch(mFeed + ".sig", b => sig = b);
            if (json == null || sig == null)
            {
                Fail(userInitiated ? "Couldn't reach the update server. Check your connection and try again." : null);
                yield break;
            }
            if (!VerifySignature(json, sig))
            {
                Debug.LogWarning("WM_UPDATE FAIL manifest signature does not verify; ignoring it");
                Fail("The update information could not be verified, so it was ignored.");
                yield break;
            }
            var doc = Json.Parse(Encoding.UTF8.GetString(json));
            string ver = Json.Str(doc, "version");
            var asset = Json.Obj(Json.Obj(doc, "assets"), PlatformKey ?? "macos");
            if (ver == null || asset == null) { Fail("The update information was incomplete."); yield break; }
            Latest = ver;
            Summary = Json.Str(doc, "summary", "");
            mAssetUrl = Json.Str(asset, "url");
            mAssetSha = (Json.Str(asset, "sha256") ?? "").ToLowerInvariant();
            mAssetFile = Json.Str(asset, "file") ?? ("Wildmere-" + ver + ".zip");
            mAssetSize = (long)Json.Num(asset, "size");
            DownloadSize = mAssetSize;
            Debug.Log($"WM_UPDATE checked current={GameRoot.Version} latest={ver} feed={mFeed}");

            if (CompareVersions(ver, GameRoot.Version) <= 0)
            {
                State = Phase.UpToDate; mFailures = 0; Notify();
                yield break;
            }
            if (mAssetUrl == null || mAssetSha.Length != 64 || mAssetSize <= 0) { Fail("The update information was incomplete."); yield break; }
            if (!mAssetUrl.StartsWith("https://") && !Cli.Has("updatefeed")) { Fail("Refusing a non-HTTPS download."); yield break; }

            string why = UnsupportedReason();
            if (why != null) { State = Phase.Unsupported; Message = why; Notify(); yield break; }

            // Already downloaded and staged in an earlier session?
            if (TryUseStaged()) { State = Phase.Ready; Notify(); if (mTestMode) StartCoroutine(TestFinish()); yield break; }

            State = Phase.Available; Notify();
            if (GameSettings.Current.AutoUpdate || mTestMode) yield return DownloadRoutine();
        }

        IEnumerator Fetch(string url, Action<byte[]> done)
        {
            using (var req = UnityWebRequest.Get(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks))
            {
                req.timeout = 30;
                req.SetRequestHeader("User-Agent", "Wildmere/" + GameRoot.Version);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) done(req.downloadHandler.data);
                else Debug.Log($"WM_UPDATE fetch failed {url}: {req.error}");
            }
        }

        void Fail(string userMessage)
        {
            mFailures++;
            State = userMessage == null ? Phase.Idle : Phase.Error;
            Message = userMessage;
            // Retry later with backoff (5, 15, 60 minutes).
            float wait = mFailures <= 1 ? 300f : mFailures == 2 ? 900f : 3600f;
            mNextCheck = Time.realtimeSinceStartup + wait;
            Notify();
        }

        public static bool VerifySignature(byte[] data, byte[] sig)
        {
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(kModulus), Exponent = new byte[] { 1, 0, 1 } });
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception e) { Debug.LogWarning($"WM_UPDATE signature check error: {e.Message}"); return false; }
        }

        /// <summary>Compare dotted versions numerically ("0.1.10" > "0.1.9"); suffixes after '-' are ignored.</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] Parse(string v)
            {
                var core = (v ?? "0").Trim().TrimStart('v').Split('-')[0].Split('.');
                var r = new int[3];
                for (int i = 0; i < 3 && i < core.Length; i++) int.TryParse(core[i], out r[i]);
                return r;
            }
            var x = Parse(a); var y = Parse(b);
            for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        // ------------------------------------------------------------------ download + stage

        string StageDir => Path.Combine(Root, Latest);

        IEnumerator DownloadRoutine()
        {
            State = Phase.Downloading; Progress = 0; Message = null; Notify();
            Directory.CreateDirectory(StageDir);
            string zip = Path.Combine(StageDir, mAssetFile);
            string part = zip + ".part";
            if (!File.Exists(zip))
            {
                if (File.Exists(part)) File.Delete(part);
                mRequest = new UnityWebRequest(mAssetUrl, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true } };
                mRequest.SetRequestHeader("User-Agent", "Wildmere/" + GameRoot.Version);
                var op = mRequest.SendWebRequest();
                while (!op.isDone)
                {
                    Progress = mRequest.downloadProgress;
                    Notify();
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                bool ok = mRequest.result == UnityWebRequest.Result.Success;
                string err = mRequest.error;
                mRequest.Dispose(); mRequest = null;
                if (!ok) { Debug.Log($"WM_UPDATE download failed: {err}"); Fail("The download didn't finish. Wildmere will try again later."); yield break; }
                File.Move(part, zip);
            }
            Progress = 1f; Notify();
            Debug.Log($"WM_UPDATE downloaded {mAssetFile}; verifying");

            // Verify size + SHA-256 off the main thread.
            var check = Task.Run(() => FileMatches(zip, mAssetSize, mAssetSha));
            while (!check.IsCompleted) yield return null;
            if (!check.Result)
            {
                Debug.LogWarning("WM_UPDATE FAIL downloaded file does not match the signed manifest; discarded");
                TryDelete(zip);
                Fail("The downloaded update was damaged and has been discarded. Wildmere will try again later.");
                yield break;
            }

            Debug.Log("WM_UPDATE verified; unpacking");
            // Extract to the staging folder (mac: ditto keeps the bundle's permissions/symlinks).
            string outDir = Path.Combine(StageDir, "app");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var ex = Task.Run(() => Extract(zip, outDir));
            while (!ex.IsCompleted) yield return null;
            if (!ex.Result || !TryUseStaged())
            {
                if (ex.Result) Debug.LogWarning($"WM_UPDATE FAIL unpacked update has no Wildmere app in {outDir}");
                Fail("The update couldn't be unpacked. Wildmere will try again later.");
                yield break;
            }
            TryDelete(zip);
            mFailures = 0;
            State = Phase.Ready; Notify();
            Debug.Log($"WM_UPDATE ready version={Latest} staged={mStagedApp}");
            if (mTestMode) StartCoroutine(TestFinish());
        }

        // Test-only: WM_UPDATE_TEST_MODE=quit captures the title screen (WM_UPDATE_SHOT) and quits normally so
        // the install-on-quit path runs; anything else installs and relaunches.
        IEnumerator TestFinish()
        {
            if (Environment.GetEnvironmentVariable("WM_UPDATE_TEST_MODE") != "quit") { InstallAndRestart(); yield break; }
            yield return new WaitForSecondsRealtime(2.5f);
            string shot = Environment.GetEnvironmentVariable("WM_UPDATE_SHOT");
            if (!string.IsNullOrEmpty(shot)) { ScreenCapture.CaptureScreenshot(shot); yield return new WaitForSecondsRealtime(1f); }
            Debug.Log("WM_UPDATE test quitting normally (install on quit)");
            Application.Quit();
        }

        static bool FileMatches(string path, long size, string sha)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length != size) return false;
                using (var s = File.OpenRead(path))
                using (var h = SHA256.Create())
                {
                    var hash = h.ComputeHash(s);
                    var sb = new StringBuilder(64);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString() == sha;
                }
            }
            catch { return false; }
        }

        static bool Extract(string zip, string outDir)
        {
            try
            {
                ProcessStartInfo psi;
                if (Application.platform == RuntimePlatform.OSXPlayer)
                    psi = new ProcessStartInfo("/usr/bin/ditto", $"-x -k \"{zip}\" \"{outDir}\"");
                else
                    // .NET's ZipFile is far faster than Expand-Archive on a ~190-file game folder.
                    psi = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory('{zip}', '{outDir}')\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    // Drain both pipes so a chatty extractor can never block on a full buffer.
                    var err = p.StandardError.ReadToEndAsync();
                    var outp = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } Debug.LogWarning("WM_UPDATE extract timed out"); return false; }
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        string msg = (err.Result + " " + outp.Result).Trim();
                        Debug.LogWarning($"WM_UPDATE extract failed: exit {p.ExitCode} {(msg.Length > 400 ? msg.Substring(0, 400) : msg)}");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception e) { Debug.LogWarning($"WM_UPDATE extract failed: {e.Message}"); return false; }
        }

        bool TryUseStaged()
        {
            if (string.IsNullOrEmpty(Latest)) return false;
            string outDir = Path.Combine(StageDir, "app");
            bool mac = Application.platform == RuntimePlatform.OSXPlayer;
            bool IsApp(string dir) => mac
                ? File.Exists(Path.Combine(dir, "Contents", "Info.plist"))
                : File.Exists(Path.Combine(dir, "Wildmere.exe")) && Directory.Exists(Path.Combine(dir, "Wildmere_Data"));
            // Release zips hold a top folder (Wildmere.app / Wildmere); accept the bare contents as well.
            string candidate = Path.Combine(outDir, mac ? "Wildmere.app" : "Wildmere");
            if (!IsApp(candidate)) candidate = IsApp(outDir) ? outDir : null;
            mStagedApp = candidate;
            return candidate != null;
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // ------------------------------------------------------------------ install

        /// <summary>Install the staged update now and relaunch (saves happen in GameRoot.OnApplicationQuit).</summary>
        public void InstallAndRestart()
        {
            if (State != Phase.Ready) return;
            if (LaunchHelper(relaunch: true)) Application.Quit();
        }

        /// <summary>Called on quit: a ready update is installed silently after the game exits.</summary>
        public void InstallOnQuit()
        {
            if (State == Phase.Ready && GameSettings.Current.AutoUpdate && !mLaunched) LaunchHelper(relaunch: false);
        }

        bool mLaunched;

        bool LaunchHelper(bool relaunch)
        {
            if (mLaunched) return true;
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(PendingPath, Json.Write(new Dictionary<string, object>
                {
                    { "from", GameRoot.Version }, { "to", Latest }, { "time", DateTime.UtcNow.ToString("o") },
                }));
                int pid = Process.GetCurrentProcess().Id;
                string target = InstallPath;
                // Test-only: arguments for the relaunched app (an env var, because they start with "-" and Cli would
                // read them as flags of this process).
                string launchArgs = mTestMode ? (Environment.GetEnvironmentVariable("WM_UPDATE_RELAUNCH_ARGS") ?? "") : "";
                ProcessStartInfo psi;
                if (Application.platform == RuntimePlatform.OSXPlayer)
                {
                    string script = Path.Combine(Root, "install-update.sh");
                    File.WriteAllText(script, kMacScript.Replace("\r\n", "\n"));
                    psi = new ProcessStartInfo("/bin/sh",
                        $"\"{script}\" {pid} \"{target}\" \"{mStagedApp}\" {(relaunch ? 1 : 0)} \"{HelperLog}\" {launchArgs}");
                }
                else
                {
                    string script = Path.Combine(Root, "install-update.ps1");
                    File.WriteAllText(script, kWinScript);
                    psi = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -ProcId {pid} -Old \"{target}\" -New \"{mStagedApp}\" -Relaunch {(relaunch ? 1 : 0)} -Log \"{HelperLog}\" -LaunchArgs \"{launchArgs}\"");
                }
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                Process.Start(psi);
                mLaunched = true;
                Debug.Log($"WM_UPDATE installing {GameRoot.Version} -> {Latest} into {target} relaunch={relaunch}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"WM_UPDATE could not start the installer: {e.Message}");
                Message = "The update couldn't be installed automatically. You can download it from the website.";
                State = Phase.Error; Notify();
                return false;
            }
        }

        /// <summary>On startup: report the result of an update installed at the last exit and clean up.</summary>
        void FinishPendingInstall()
        {
            try
            {
                if (!File.Exists(PendingPath)) { CleanupOld(); return; }
                var doc = Json.Parse(File.ReadAllText(PendingPath));
                string from = Json.Str(doc, "from"), to = Json.Str(doc, "to");
                File.Delete(PendingPath);
                if (to != null && CompareVersions(GameRoot.Version, to) >= 0)
                {
                    JustUpdatedFrom = from;
                    Debug.Log($"WM_UPDATE installed from={from} to={GameRoot.Version}");
                }
                else Debug.LogWarning($"WM_UPDATE FAIL the update to {to} did not install (still {GameRoot.Version}); see {HelperLog}");
                CleanupOld();
            }
            catch (Exception e) { Debug.LogWarning($"WM_UPDATE pending check: {e.Message}"); }
        }

        static void CleanupOld()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var d in Directory.GetDirectories(Root))
                    if (CompareVersions(Path.GetFileName(d), GameRoot.Version) <= 0) Directory.Delete(d, true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ helper scripts

        // args: pid old_app new_app relaunch(0/1) log [launch args...]
        const string kMacScript = @"#!/bin/sh
PID=""$1""; OLD=""$2""; NEW=""$3""; RELAUNCH=""$4""; LOG=""$5""; shift 5
exec >>""$LOG"" 2>&1
echo ""$(date) wildmere update: waiting for $PID to exit""
i=0
while kill -0 ""$PID"" 2>/dev/null; do sleep 0.2; i=$((i+1)); if [ $i -gt 900 ]; then echo 'timed out waiting'; exit 1; fi; done
BAK=""$OLD.previous""
rm -rf ""$BAK""
if ! mv ""$OLD"" ""$BAK""; then echo 'could not move the old app aside'; [ ""$RELAUNCH"" = 1 ] && open ""$OLD""; exit 1; fi
if ! mv ""$NEW"" ""$OLD""; then echo 'could not move the new app in; restoring'; mv ""$BAK"" ""$OLD""; [ ""$RELAUNCH"" = 1 ] && open ""$OLD""; exit 1; fi
xattr -dr com.apple.quarantine ""$OLD"" 2>/dev/null
rm -rf ""$BAK""
echo 'installed'
if [ ""$RELAUNCH"" = 1 ]; then
  if [ $# -gt 0 ]; then open -n ""$OLD"" --args ""$@""; else open -n ""$OLD""; fi
  rc=$?; echo ""relaunch via open: exit $rc""
  if [ $rc -ne 0 ]; then nohup ""$OLD/Contents/MacOS/Wildmere"" ""$@"" >/dev/null 2>&1 & echo 'relaunched the binary directly'; fi
fi
exit 0
";

        const string kWinScript = @"param([int]$ProcId, [string]$Old, [string]$New, [int]$Relaunch, [string]$Log, [string]$LaunchArgs)
function Say($m) { Add-Content -LiteralPath $Log -Value ((Get-Date -Format o) + ' ' + $m) }
Say ""wildmere update: waiting for $ProcId to exit""
try { Wait-Process -Id $ProcId -Timeout 180 -ErrorAction SilentlyContinue } catch {}
$bak = $Old + '.previous'
if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Recurse -Force -ErrorAction SilentlyContinue }
$moved = $false
for ($i = 0; $i -lt 40 -and -not $moved; $i++) {
  try { Move-Item -LiteralPath $Old -Destination $bak -ErrorAction Stop; $moved = $true } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $moved) { Say 'could not move the old version aside'; if ($Relaunch -eq 1) { Start-Process -FilePath (Join-Path $Old 'Wildmere.exe') }; exit 1 }
try { Move-Item -LiteralPath $New -Destination $Old -ErrorAction Stop }
catch { Say ('could not move the new version in: ' + $_); Move-Item -LiteralPath $bak -Destination $Old; if ($Relaunch -eq 1) { Start-Process -FilePath (Join-Path $Old 'Wildmere.exe') }; exit 1 }
Remove-Item -LiteralPath $bak -Recurse -Force -ErrorAction SilentlyContinue
Say 'installed'
if ($Relaunch -eq 1) {
  if ($LaunchArgs) { Start-Process -FilePath (Join-Path $Old 'Wildmere.exe') -ArgumentList $LaunchArgs } else { Start-Process -FilePath (Join-Path $Old 'Wildmere.exe') }
}
exit 0
";
    }
}
