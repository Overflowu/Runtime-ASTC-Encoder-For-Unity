using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Aperture.Astcenc.Editor
{
    /// <summary>
    /// Command-line and Editor build entry points for multi-platform CI builds (GameCI / batchmode).
    /// </summary>
    public static class CIBuildScript
    {
        private const string E2EDefineSymbol = "ASTC_CI_E2E";

        /// <summary>
        /// Main entry point invoked by GameCI (<c>buildMethod: Aperture.Astcenc.Editor.CIBuildScript.Build</c>)
        /// or via <c>Unity -batchmode -quit -executeMethod Aperture.Astcenc.Editor.CIBuildScript.Build</c>.
        /// </summary>
        public static void Build()
        {
            Dictionary<string, string> args = ParseCommandLineArgs();

            string targetName = GetArg(args, "customBuildTarget", GetArg(args, "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString()));
            if (!TryParseBuildTarget(targetName, out BuildTarget buildTarget))
            {
                Fail($"Unsupported buildTarget: '{targetName}'.");
                return;
            }

            string buildName = GetArg(args, "customBuildName", PlayerSettings.productName);
            string outputPath = GetArg(args, "customBuildPath", Path.Combine("build", buildTarget.ToString(), buildName));
            string scriptingBackendArg = GetArg(args, "scriptingBackend", string.Empty);
            bool enableE2ESmoke = HasFlag(args, "enableE2ESmoke");
            bool isDevelopment = HasFlag(args, "development");

            ExecuteBuild(buildTarget, outputPath, buildName, scriptingBackendArg, enableE2ESmoke, isDevelopment);
        }

        [MenuItem("Build/CI/Android (ARM64 IL2CPP)")]
        public static void BuildAndroidLocal()
        {
            ExecuteBuild(BuildTarget.Android, "build/Android/ASTCEncoder.apk", "ASTCEncoder", "IL2CPP", enableE2ESmoke: false, isDevelopment: false);
        }

        [MenuItem("Build/CI/iOS (Xcode Project)")]
        public static void BuildiOSLocal()
        {
            ExecuteBuild(BuildTarget.iOS, "build/iOS", "ASTCEncoder", "IL2CPP", enableE2ESmoke: false, isDevelopment: false);
        }

        [MenuItem("Build/CI/Windows x64 (Mono)")]
        public static void BuildWindows64Local()
        {
            ExecuteBuild(BuildTarget.StandaloneWindows64, "build/StandaloneWindows64/ASTCEncoder.exe", "ASTCEncoder", "Mono", enableE2ESmoke: false, isDevelopment: false);
        }

        [MenuItem("Build/CI/macOS Universal (Mono)")]
        public static void BuildOSXLocal()
        {
            ExecuteBuild(BuildTarget.StandaloneOSX, "build/StandaloneOSX/ASTCEncoder.app", "ASTCEncoder", "Mono", enableE2ESmoke: false, isDevelopment: false);
        }

        [MenuItem("Build/CI/Linux x64 (Mono)")]
        public static void BuildLinux64Local()
        {
            ExecuteBuild(BuildTarget.StandaloneLinux64, "build/StandaloneLinux64/ASTCEncoder.x86_64", "ASTCEncoder", "Mono", enableE2ESmoke: false, isDevelopment: false);
        }

        private static void ExecuteBuild(
            BuildTarget buildTarget,
            string outputPath,
            string buildName,
            string scriptingBackendArg,
            bool enableE2ESmoke,
            bool isDevelopment)
        {
            BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Fail("No enabled scenes found in EditorBuildSettings.");
                return;
            }

            ConfigurePlatformSettings(buildTarget, targetGroup, scriptingBackendArg);

            string originalDefines = PlayerSettings.GetScriptingDefineSymbolsForGroup(targetGroup);
            if (enableE2ESmoke)
            {
                AddDefineSymbol(targetGroup, E2EDefineSymbol);
            }

            string locationPath = NormalizeOutputPath(buildTarget, outputPath, buildName);
            EnsureParentDirectory(buildTarget, locationPath);

            Debug.Log($"[CIBuildScript] Building target={buildTarget}, group={targetGroup}, backend={PlayerSettings.GetScriptingBackend(targetGroup)}, e2e={enableE2ESmoke}, output='{locationPath}'");

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = locationPath,
                target = buildTarget,
                targetGroup = targetGroup,
                options = isDevelopment ? BuildOptions.Development : BuildOptions.None,
            };

            try
            {
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log($"[CIBuildScript] Build finished: result={summary.result}, errors={summary.totalErrors}, warnings={summary.totalWarnings}, size={summary.totalSize} bytes, time={summary.totalTime}");

                if (summary.result != BuildResult.Succeeded)
                {
                    Fail($"Build failed for {buildTarget} with result {summary.result} ({summary.totalErrors} errors).");
                }
            }
            finally
            {
                if (enableE2ESmoke)
                {
                    PlayerSettings.SetScriptingDefineSymbolsForGroup(targetGroup, originalDefines);
                }
            }
        }

        private static void ConfigurePlatformSettings(BuildTarget buildTarget, BuildTargetGroup targetGroup, string scriptingBackendArg)
        {
            if (string.Equals(scriptingBackendArg, "Mono", StringComparison.OrdinalIgnoreCase))
            {
                PlayerSettings.SetScriptingBackend(targetGroup, ScriptingImplementation.Mono2x);
            }
            else if (string.Equals(scriptingBackendArg, "IL2CPP", StringComparison.OrdinalIgnoreCase))
            {
                PlayerSettings.SetScriptingBackend(targetGroup, ScriptingImplementation.IL2CPP);
            }
            else if (buildTarget == BuildTarget.StandaloneWindows64 || buildTarget == BuildTarget.StandaloneOSX)
            {
                // Cross-building Windows/macOS from Linux Editor only supports Mono.
#if UNITY_EDITOR_LINUX
                PlayerSettings.SetScriptingBackend(targetGroup, ScriptingImplementation.Mono2x);
#endif
            }

            switch (buildTarget)
            {
                case BuildTarget.Android:
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                    EditorUserBuildSettings.buildAppBundle = false;
                    break;

                case BuildTarget.iOS:
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
                    PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
                    break;
            }
        }

        private static string NormalizeOutputPath(BuildTarget buildTarget, string outputPath, string buildName)
        {
            string normalized = outputPath.TrimEnd('/', '\\');
            switch (buildTarget)
            {
                case BuildTarget.Android:
                    if (!normalized.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
                        !normalized.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = Path.Combine(normalized, buildName + ".apk");
                    }
                    break;

                case BuildTarget.StandaloneWindows64:
                    if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = Path.Combine(normalized, buildName + ".exe");
                    }
                    break;

                case BuildTarget.StandaloneOSX:
                    if (!normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = Path.Combine(normalized, buildName + ".app");
                    }
                    break;

                case BuildTarget.StandaloneLinux64:
                    if (Directory.Exists(normalized) || !Path.HasExtension(normalized))
                    {
                        // If outputPath looks like a folder (e.g. build/StandaloneLinux64), append executable name.
                        if (!normalized.EndsWith(buildName, StringComparison.OrdinalIgnoreCase))
                        {
                            normalized = Path.Combine(normalized, buildName + ".x86_64");
                        }
                    }
                    break;
            }

            return normalized;
        }

        private static void EnsureParentDirectory(BuildTarget buildTarget, string locationPath)
        {
            if (buildTarget == BuildTarget.iOS)
            {
                Directory.CreateDirectory(locationPath);
            }
            else
            {
                string dir = Path.GetDirectoryName(locationPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }

        private static void AddDefineSymbol(BuildTargetGroup targetGroup, string symbol)
        {
            string existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(targetGroup);
            List<string> symbols = existing
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            if (!symbols.Contains(symbol))
            {
                symbols.Add(symbol);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(targetGroup, string.Join(";", symbols));
            }
        }

        private static bool TryParseBuildTarget(string targetName, out BuildTarget buildTarget)
        {
            if (Enum.TryParse(targetName, ignoreCase: true, out buildTarget))
            {
                return true;
            }

            switch (targetName.ToLowerInvariant())
            {
                case "win64":
                case "windows":
                case "windows64":
                    buildTarget = BuildTarget.StandaloneWindows64;
                    return true;
                case "osx":
                case "macos":
                case "osxuniversal":
                    buildTarget = BuildTarget.StandaloneOSX;
                    return true;
                case "linux":
                case "linux64":
                    buildTarget = BuildTarget.StandaloneLinux64;
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, string> ParseCommandLineArgs()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = arg.TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
            return result;
        }

        private static string GetArg(Dictionary<string, string> args, string key, string defaultValue)
        {
            return args.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value)
                ? value
                : defaultValue;
        }

        private static bool HasFlag(Dictionary<string, string> args, string key)
        {
            return args.TryGetValue(key, out string value) &&
                   !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static void Fail(string message)
        {
            Debug.LogError($"[CIBuildScript] {message}");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
            else
            {
                throw new Exception(message);
            }
        }
    }
}
