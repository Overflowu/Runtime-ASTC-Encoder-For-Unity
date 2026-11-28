using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Aperture.Astcenc.Editor
{
    /// <summary>
    /// Unity Editor build processor & menu utilities for WebGL & Tuanjie MiniGame builds:
    /// - Injects WASM SIMD128 flags (<c>-msimd128 -msse4.1</c>) when <c>ASTC_WEBGL_WASM_SIMD</c> is enabled.
    /// - Propagates <c>ASTC_WEBGL_PTHREADS</c> when <see cref="PlayerSettings.WebGL.threadsSupport"/> is enabled.
    /// - Bundles prebuilt Web Worker JS/WASM assets and writes runtime auto-init config.
    /// - Configures <c>workers</c> in <c>game.json</c> for Tuanjie MiniGame exports.
    /// </summary>
    public sealed class AstcWebGLBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public const string WasmSimdDefineSymbol = "ASTC_WEBGL_WASM_SIMD";
        public const string PthreadsDefineSymbol = "ASTC_WEBGL_PTHREADS";

        private const string MenuPathToggleSimd = "Window/ASTC Encoder/WebGL/Enable WASM SIMD (-msimd128 -msse4.1)";
        private const string SimdFlags = "-msimd128 -msse4.1";
        private const string TempResourcesRoot = "Assets/AstcWebRuntimeConfig_Generated";
        private const string TempConfigAssetPath = TempResourcesRoot + "/Resources/AstcWebRuntimeConfig.json";

        private static string s_OriginalEmscriptenArgs;
        private static bool s_InjectedSimdFlags;
        private static bool s_CreatedTempConfig;

        [Serializable]
        private sealed class RuntimeConfigJson
        {
            public bool autoInit = true;
            public int maxWorkers = 0;
            public string workerUrl = "";
        }

        public int callbackOrder => 0;

        [MenuItem(MenuPathToggleSimd)]
        private static void ToggleWasmSimd()
        {
            bool enabled = IsWasmSimdEnabled();
            SetWasmSimdEnabled(!enabled);
            Debug.Log($"[ASTC Encoder] WebGL WASM SIMD ({WasmSimdDefineSymbol}) {(!enabled ? "ENABLED" : "DISABLED")}.");
        }

        [MenuItem(MenuPathToggleSimd, validate = true)]
        private static bool ValidateToggleWasmSimd()
        {
            Menu.SetChecked(MenuPathToggleSimd, IsWasmSimdEnabled());
            return true;
        }

        public static bool IsWasmSimdEnabled()
        {
            return HasDefine(BuildTargetGroup.WebGL, WasmSimdDefineSymbol);
        }

        public static void SetWasmSimdEnabled(bool enabled)
        {
            SetDefine(BuildTargetGroup.WebGL, WasmSimdDefineSymbol, enabled);
        }

        private static bool IsWebOrMiniGameTarget(BuildTarget target, out bool isMiniGame)
        {
            string name = target.ToString();
            isMiniGame = string.Equals(name, "MiniGame", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(name, "WeixinMiniGame", StringComparison.OrdinalIgnoreCase);
            return target == BuildTarget.WebGL || isMiniGame;
        }

        private static bool HasDefine(BuildTargetGroup group, string symbol)
        {
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            return defines
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(s => string.Equals(s.Trim(), symbol, StringComparison.Ordinal));
        }

        private static void SetDefine(BuildTargetGroup group, string symbol, bool enabled)
        {
            string existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            List<string> symbols = existing
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            bool currentlyEnabled = symbols.Contains(symbol);
            if (enabled && !currentlyEnabled)
            {
                symbols.Add(symbol);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", symbols));
            }
            else if (!enabled && currentlyEnabled)
            {
                symbols.RemoveAll(s => string.Equals(s, symbol, StringComparison.Ordinal));
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", symbols));
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            s_InjectedSimdFlags = false;
            s_CreatedTempConfig = false;

            if (!IsWebOrMiniGameTarget(report.summary.platform, out bool isMiniGame))
                return;

            // 1. Propagate pthreads define (standard WebGL only)
            bool pthreadsEnabled = !isMiniGame && PlayerSettings.WebGL.threadsSupport;
            SetDefine(report.summary.platformGroup, PthreadsDefineSymbol, pthreadsEnabled);

            // 2. Inject WASM SIMD flags
            if (IsWasmSimdEnabled())
            {
                s_OriginalEmscriptenArgs = PlayerSettings.WebGL.emscriptenArgs ?? string.Empty;
                string updated = s_OriginalEmscriptenArgs;
                if (updated.IndexOf("-msimd128", StringComparison.Ordinal) < 0)
                {
                    updated = string.IsNullOrWhiteSpace(updated) ? "-msimd128" : (updated + " -msimd128");
                }
                if (updated.IndexOf("-msse4.1", StringComparison.Ordinal) < 0)
                {
                    updated = updated + " -msse4.1";
                }

                if (!string.Equals(updated, s_OriginalEmscriptenArgs, StringComparison.Ordinal))
                {
                    PlayerSettings.WebGL.emscriptenArgs = updated;
                    s_InjectedSimdFlags = true;
                    Debug.Log($"[ASTC Encoder] Injected WebGL WASM SIMD flags ({SimdFlags}) into PlayerSettings.WebGL.emscriptenArgs.");
                }
            }

            // 3. Write temporary runtime config for worker auto-initialization
            AstcWebBuildSettingsData settings = AstcWebBuildSettings.Load();
            if (settings.bundleWebWorker && settings.autoInitializeWorker)
            {
                try
                {
                    string dir = Path.GetDirectoryName(TempConfigAssetPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    var cfg = new RuntimeConfigJson
                    {
                        autoInit = true,
                        maxWorkers = settings.maxWorkers,
                        workerUrl = isMiniGame ? "workers/astcenc/astcenc-worker-minigame.js" : ""
                    };
                    File.WriteAllText(TempConfigAssetPath, JsonUtility.ToJson(cfg));
                    AssetDatabase.Refresh();
                    s_CreatedTempConfig = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ASTC Encoder] Failed to create temporary Web runtime config: {ex.Message}");
                }
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (s_InjectedSimdFlags)
            {
                PlayerSettings.WebGL.emscriptenArgs = s_OriginalEmscriptenArgs ?? string.Empty;
                s_InjectedSimdFlags = false;
                s_OriginalEmscriptenArgs = null;
            }

            if (s_CreatedTempConfig)
            {
                s_CreatedTempConfig = false;
                try
                {
                    if (AssetDatabase.IsValidFolder(TempResourcesRoot))
                    {
                        AssetDatabase.DeleteAsset(TempResourcesRoot);
                        AssetDatabase.Refresh();
                    }
                    else if (Directory.Exists(TempResourcesRoot))
                    {
                        Directory.Delete(TempResourcesRoot, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ASTC Encoder] Failed to clean up {TempResourcesRoot}: {ex.Message}");
                }
            }

            if (!IsWebOrMiniGameTarget(report.summary.platform, out bool isMiniGame))
                return;

            AstcWebBuildSettingsData settings = AstcWebBuildSettings.Load();
            if (!settings.bundleWebWorker)
                return;

            string outputPath = report.summary.outputPath;
            if (string.IsNullOrEmpty(outputPath) || !Directory.Exists(outputPath))
                return;

            string workerSrcDir = LocateWorkerSourceDirectory();
            if (string.IsNullOrEmpty(workerSrcDir))
            {
                Debug.LogWarning("[ASTC Encoder] Prebuilt Web Worker assets (Plugins/WebGL/Worker~) were not found. " +
                                 "Run .github/scripts/build-webgl-worker.sh to build them, or call InitializeWebWorkerAsync manually.");
                return;
            }

            if (isMiniGame)
            {
                CopyMiniGameWorkerAssets(workerSrcDir, outputPath, settings);
            }
            else
            {
                CopyStandardWebWorkerAssets(workerSrcDir, outputPath);
            }
        }

        private static string LocateWorkerSourceDirectory()
        {
            string[] candidates =
            {
                Path.GetFullPath("Assets/ASTC Encoder Web/Plugins/WebGL/Worker~"),
                Path.GetFullPath("Packages/com.aperture.astcenc.web/Plugins/WebGL/Worker~"),
                Path.GetFullPath("Assets/ASTC Encoder/Plugins/WebGL/Worker~"),
                Path.GetFullPath("Packages/com.aperture.astcenc/Plugins/WebGL/Worker~"),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (Directory.Exists(candidates[i]) && File.Exists(Path.Combine(candidates[i], "astcenc-worker.js")))
                    return candidates[i];
            }

            return null;
        }

        private static void CopyStandardWebWorkerAssets(string srcDir, string outputPath)
        {
            string dstDir = Path.Combine(outputPath, "StreamingAssets", "AstcWebWorker");
            Directory.CreateDirectory(dstDir);

            string[] files =
            {
                "astcenc-worker.js",
                "astcenc-worker-scalar.js",
                "astcenc-worker-scalar.wasm",
                "astcenc-worker-simd.js",
                "astcenc-worker-simd.wasm",
            };

            int copied = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string src = Path.Combine(srcDir, files[i]);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(dstDir, files[i]), overwrite: true);
                    copied++;
                }
            }

            Debug.Log($"[ASTC Encoder] Bundled {copied} Web Worker file(s) into {dstDir}.");
        }

        private static void CopyMiniGameWorkerAssets(string srcDir, string outputPath, AstcWebBuildSettingsData settings)
        {
            string dstDir = Path.Combine(outputPath, "workers", "astcenc");
            Directory.CreateDirectory(dstDir);

            var files = new List<string> { "astcenc-worker-minigame.js" };
            if (settings.miniGameWorkerVariant != MiniGameWorkerBundleMode.SimdOnly)
            {
                files.Add("astcenc-worker-scalar.js");
                files.Add("astcenc-worker-scalar.wasm");
            }
            if (settings.miniGameWorkerVariant != MiniGameWorkerBundleMode.ScalarOnly)
            {
                files.Add("astcenc-worker-simd.js");
                files.Add("astcenc-worker-simd.wasm");
            }

            int copied = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string src = Path.Combine(srcDir, files[i]);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(dstDir, files[i]), overwrite: true);
                    copied++;
                }
            }

            Debug.Log($"[ASTC Encoder] Bundled {copied} MiniGame Worker file(s) into {dstDir}.");

            if (settings.miniGameInjectGameJson)
            {
                InjectMiniGameJson(outputPath);
            }
        }

        private static void InjectMiniGameJson(string outputPath)
        {
            string gameJsonPath = Path.Combine(outputPath, "game.json");
            if (!File.Exists(gameJsonPath))
                return;

            try
            {
                string json = File.ReadAllText(gameJsonPath);
                if (Regex.IsMatch(json, "\"workers\"\\s*:"))
                    return;

                int lastBrace = json.LastIndexOf('}');
                if (lastBrace < 0)
                    return;

                string prefix = json.Substring(0, lastBrace).TrimEnd();
                bool needsComma = prefix.Length > 0 && prefix[prefix.Length - 1] != '{' && prefix[prefix.Length - 1] != ',';
                string updated = prefix + (needsComma ? "," : "") + "\n  \"workers\": \"workers\"\n}";
                File.WriteAllText(gameJsonPath, updated);
                Debug.Log($"[ASTC Encoder] Injected \"workers\": \"workers\" into {gameJsonPath}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASTC Encoder] Failed to inject workers into game.json: {ex.Message}");
            }
        }
    }
}
