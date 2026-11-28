using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Aperture.Astcenc.Editor
{
    public enum MiniGameWorkerBundleMode
    {
        Both = 0,
        SimdOnly = 1,
        ScalarOnly = 2,
    }

    [Serializable]
    public sealed class AstcWebBuildSettingsData
    {
        public bool bundleWebWorker = true;
        public bool autoInitializeWorker = true;
        public int maxWorkers = 0;
        public bool miniGameInjectGameJson = true;
        public MiniGameWorkerBundleMode miniGameWorkerVariant = MiniGameWorkerBundleMode.Both;
    }

    public static class AstcWebBuildSettings
    {
        private const string SettingsPath = "ProjectSettings/AstcEncoderWeb.json";

        private static AstcWebBuildSettingsData s_Cached;

        public static AstcWebBuildSettingsData Load()
        {
            if (s_Cached != null)
                return s_Cached;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    s_Cached = JsonUtility.FromJson<AstcWebBuildSettingsData>(json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASTC Encoder] Failed to read {SettingsPath}: {ex.Message}");
            }

            if (s_Cached == null)
                s_Cached = new AstcWebBuildSettingsData();

            return s_Cached;
        }

        public static void Save(AstcWebBuildSettingsData data)
        {
            s_Cached = data ?? new AstcWebBuildSettingsData();
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(s_Cached, prettyPrint: true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASTC Encoder] Failed to save {SettingsPath}: {ex.Message}");
            }
        }

        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            var provider = new SettingsProvider("Project/ASTC Encoder/Web", SettingsScope.Project)
            {
                label = "Web & MiniGame",
                guiHandler = _ =>
                {
                    AstcWebBuildSettingsData data = Load();
                    EditorGUI.BeginChangeCheck();

                    EditorGUILayout.LabelField("WASM & Threading", EditorStyles.boldLabel);
                    bool simd = AstcWebGLBuildProcessor.IsWasmSimdEnabled();
                    bool newSimd = EditorGUILayout.Toggle("Enable WASM SIMD128", simd);
                    if (newSimd != simd)
                    {
                        AstcWebGLBuildProcessor.SetWasmSimdEnabled(newSimd);
                    }
                    EditorGUILayout.HelpBox(
                        $"Native C/C++ Multithreading (pthreads): {(PlayerSettings.WebGL.threadsSupport ? "ENABLED" : "DISABLED")} (configured in Player Settings > WebGL > Publishing Settings).",
                        MessageType.Info);

                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Web Worker Pool", EditorStyles.boldLabel);
                    data.bundleWebWorker = EditorGUILayout.Toggle("Bundle Worker Assets", data.bundleWebWorker);
                    using (new EditorGUI.DisabledScope(!data.bundleWebWorker))
                    {
                        data.autoInitializeWorker = EditorGUILayout.Toggle("Auto-Initialize on Startup", data.autoInitializeWorker);
                        data.maxWorkers = EditorGUILayout.IntSlider("Max Workers (0 = Auto)", data.maxWorkers, 0, 8);
                    }

                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Tuanjie MiniGame", EditorStyles.boldLabel);
                    data.miniGameInjectGameJson = EditorGUILayout.Toggle("Inject game.json workers", data.miniGameInjectGameJson);
                    data.miniGameWorkerVariant = (MiniGameWorkerBundleMode)EditorGUILayout.EnumPopup("MiniGame Worker WASM", data.miniGameWorkerVariant);

                    if (EditorGUI.EndChangeCheck())
                    {
                        Save(data);
                    }
                }
            };
            return provider;
        }
    }
}
