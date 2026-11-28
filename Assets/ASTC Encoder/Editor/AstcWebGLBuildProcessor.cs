using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Aperture.Astcenc.Editor
{
    /// <summary>
    /// Unity Editor build processor & menu utilities for WebGL WASM SIMD128 acceleration.
    /// When <c>ASTC_WEBGL_WASM_SIMD</c> is enabled for WebGL, automatically appends <c>-msimd128 -msse4.1</c>
    /// to <see cref="PlayerSettings.WebGL.emscriptenArgs"/> during WebGL player builds so Emscripten compiles
    /// astc-encoder using 128-bit WebAssembly SIMD instructions (via SSE4.1 intrinsic translation) and restores
    /// the user's original flags after the build completes.
    /// </summary>
    public sealed class AstcWebGLBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public const string WasmSimdDefineSymbol = "ASTC_WEBGL_WASM_SIMD";
        private const string MenuPathToggleSimd = "Window/ASTC Encoder/WebGL/Enable WASM SIMD (-msimd128 -msse4.1)";
        private const string SimdFlags = "-msimd128 -msse4.1";

        private static string s_OriginalEmscriptenArgs;
        private static bool s_InjectedSimdFlags;

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
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.WebGL);
            return defines
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(s => string.Equals(s.Trim(), WasmSimdDefineSymbol, StringComparison.Ordinal));
        }

        public static void SetWasmSimdEnabled(bool enabled)
        {
            string existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.WebGL);
            List<string> symbols = existing
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            bool currentlyEnabled = symbols.Contains(WasmSimdDefineSymbol);
            if (enabled && !currentlyEnabled)
            {
                symbols.Add(WasmSimdDefineSymbol);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.WebGL, string.Join(";", symbols));
            }
            else if (!enabled && currentlyEnabled)
            {
                symbols.RemoveAll(s => string.Equals(s, WasmSimdDefineSymbol, StringComparison.Ordinal));
                PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.WebGL, string.Join(";", symbols));
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            s_InjectedSimdFlags = false;
            if (report.summary.platform != BuildTarget.WebGL || !IsWasmSimdEnabled())
                return;

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

        public void OnPostprocessBuild(BuildReport report)
        {
            if (s_InjectedSimdFlags)
            {
                PlayerSettings.WebGL.emscriptenArgs = s_OriginalEmscriptenArgs ?? string.Empty;
                s_InjectedSimdFlags = false;
                s_OriginalEmscriptenArgs = null;
            }
        }
    }
}
