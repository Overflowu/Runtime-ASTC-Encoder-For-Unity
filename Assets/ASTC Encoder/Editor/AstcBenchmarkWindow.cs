using System;
using System.Diagnostics;
using System.IO;
using Aperture.Astcenc.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

namespace Aperture.Astcenc.Editor
{
    /// <summary>
    /// Unity Editor window for visually inspecting and benchmarking runtime ASTC compression
    /// (CPU vs GPU side-by-side), computing PSNR / RMSE / Normal Angular Error, and viewing amplified error heatmaps.
    /// </summary>
    public class AstcBenchmarkWindow : EditorWindow
    {
        private enum BenchmarkMode
        {
            CompareCpuAndGpu,
            GpuOnly,
            CpuOnly
        }

        private sealed class BenchmarkResult : IDisposable
        {
            public string Label;
            public double EncodeMs;
            public int CompressedBytes;
            public int UncompressedBytes;
            public double RgbPsnrDb;
            public double RgbaPsnrDb;
            public double RgbRmse;
            public double AlphaRmse;
            public double MeanNormalDeg;
            public double MaxNormalDeg;
            public Texture2D DecodedPreview;
            public Texture2D HeatmapPreview;

            public void Dispose()
            {
                if (DecodedPreview != null)
                {
                    DestroyImmediate(DecodedPreview);
                    DecodedPreview = null;
                }
                if (HeatmapPreview != null)
                {
                    DestroyImmediate(HeatmapPreview);
                    HeatmapPreview = null;
                }
            }
        }

        private Texture2D m_SourceTexture;
        private TextureType m_TextureType = TextureType.Default;
        private BlockSize m_BlockSize = BlockSize.ASTC_6x6;
        private CompressQuality m_Quality = CompressQuality.Fast;
        private bool m_UseAlphaWeight;
        private bool m_UsePerceptual;
        private bool m_GenerateMipmaps;
        private BenchmarkMode m_Mode = BenchmarkMode.CompareCpuAndGpu;
        private float m_HeatmapGain = 10f;
        private bool m_ShowAlphaOnly;
        private Vector2 m_ScrollPos;

        private Texture2D m_ReferencePreview;
        private BenchmarkResult m_GpuResult;
        private BenchmarkResult m_CpuResult;
        private string m_StatusMessage;

        [MenuItem("Window/ASTC Encoder/Benchmark & Inspector")]
        public static void OpenWindow()
        {
            var window = GetWindow<AstcBenchmarkWindow>("ASTC Benchmark");
            window.minSize = new Vector2(720f, 520f);
            window.Show();
        }

        private void OnDisable()
        {
            ClearResults();
        }

        private void ClearResults()
        {
            if (m_ReferencePreview != null)
            {
                DestroyImmediate(m_ReferencePreview);
                m_ReferencePreview = null;
            }
            m_GpuResult?.Dispose();
            m_GpuResult = null;
            m_CpuResult?.Dispose();
            m_CpuResult = null;
        }

        private void OnGUI()
        {
            m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos);

            EditorGUILayout.LabelField("Runtime ASTC Encoder — Visual Benchmark & Quality Inspector", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                m_SourceTexture = (Texture2D)EditorGUILayout.ObjectField("Source Texture2D", m_SourceTexture, typeof(Texture2D), false);
                m_Mode = (BenchmarkMode)EditorGUILayout.EnumPopup("Backend Mode", m_Mode);
                m_TextureType = (TextureType)EditorGUILayout.EnumPopup("Texture Type", m_TextureType);
                m_BlockSize = (BlockSize)EditorGUILayout.EnumPopup("Block Size", m_BlockSize);
                m_Quality = (CompressQuality)EditorGUILayout.EnumPopup("Quality", m_Quality);
                m_UseAlphaWeight = EditorGUILayout.Toggle("Use Alpha Weight", m_UseAlphaWeight);
                m_UsePerceptual = EditorGUILayout.Toggle("Perceptual (CPU)", m_UsePerceptual);
                m_GenerateMipmaps = EditorGUILayout.Toggle("Generate Mipmaps (GPU)", m_GenerateMipmaps);
                m_HeatmapGain = EditorGUILayout.Slider("Error Heatmap Gain", m_HeatmapGain, 1f, 32f);
                m_ShowAlphaOnly = EditorGUILayout.Toggle("Preview Alpha Channel", m_ShowAlphaOnly);
            }

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = m_SourceTexture != null;
                if (GUILayout.Button("Run Benchmark", GUILayout.Height(30f)))
                {
                    RunBenchmark();
                }
                if (GUILayout.Button("Export .ktx", GUILayout.Height(30f), GUILayout.Width(110f)))
                {
                    ExportFile("ktx");
                }
                if (GUILayout.Button("Export .astc", GUILayout.Height(30f), GUILayout.Width(110f)))
                {
                    ExportFile("astc");
                }
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(m_StatusMessage))
            {
                EditorGUILayout.HelpBox(m_StatusMessage, MessageType.Info);
            }

            if (m_GpuResult != null)
                DrawResultSection(m_GpuResult);

            if (m_CpuResult != null)
                DrawResultSection(m_CpuResult);

            EditorGUILayout.EndScrollView();
        }

        private void RunBenchmark()
        {
            ClearResults();
            m_StatusMessage = null;

            if (m_SourceTexture == null)
                return;

            int width = m_SourceTexture.width;
            int height = m_SourceTexture.height;
            bool isHdr = GraphicsFormatUtility.IsFloatFormat(m_SourceTexture.graphicsFormat);
            bool isSrgb = m_TextureType == TextureType.Default && GraphicsFormatUtility.IsSRGBFormat(m_SourceTexture.graphicsFormat);

            Texture2D readableSource = CreateReadableCopy(m_SourceTexture, isSrgb);
            try
            {
                Color[] refPixels = readableSource.GetPixels(0);
                m_ReferencePreview = BuildPreviewTexture(refPixels, width, height, m_ShowAlphaOnly);

                if (m_Mode == BenchmarkMode.CompareCpuAndGpu || m_Mode == BenchmarkMode.GpuOnly)
                {
                    if (Encoder.IsGpuSupported)
                    {
                        m_GpuResult = ExecuteBackend(readableSource, refPixels, EncodeBackend.Gpu, "GPU Compute", isSrgb, isHdr);
                    }
                    else
                    {
                        m_StatusMessage = "GPU Compute backend is not supported on the current graphics device.";
                    }
                }

                if (m_Mode == BenchmarkMode.CompareCpuAndGpu || m_Mode == BenchmarkMode.CpuOnly)
                {
                    m_CpuResult = ExecuteBackend(readableSource, refPixels, EncodeBackend.Cpu, "CPU Native (astcenc)", isSrgb, isHdr);
                }
            }
            catch (Exception ex)
            {
                m_StatusMessage = $"Benchmark failed: {ex.Message}";
                Debug.LogException(ex);
            }
            finally
            {
                DestroyImmediate(readableSource);
            }
        }

        private BenchmarkResult ExecuteBackend(
            Texture2D readableSource,
            Color[] refPixels,
            EncodeBackend backend,
            string label,
            bool isSrgb,
            bool isHdr)
        {
            int width = readableSource.width;
            int height = readableSource.height;
            var options = new AstcCompressOptions
            {
                TextureType = m_TextureType,
                BlockSize = m_BlockSize,
                Quality = m_Quality,
                IncludeMipmaps = false,
                GenerateMipmaps = m_GenerateMipmaps && backend == EncodeBackend.Gpu,
                UseAlphaWeight = m_UseAlphaWeight,
                UsePerceptual = m_UsePerceptual,
                ThreadCount = Math.Max(1, Environment.ProcessorCount / 2),
                Backend = backend
            };

            var sw = Stopwatch.StartNew();
            using AstcRaw raw = readableSource.CompressTexture(options);
            sw.Stop();

            if (raw == null)
            {
                m_StatusMessage = $"{label} returned null (backend unsupported or failed).";
                return null;
            }

            // Software-decode mip 0 via native astcenc so metrics & previews work on any PC/Mac Editor.
            byte[] decodedRgba = Encoder.Decompress(raw, width, height, m_BlockSize, srgb: isSrgb, isHdr: isHdr, outputBitness: isHdr ? 32 : 8);
            if (decodedRgba == null)
            {
                m_StatusMessage = $"{label} encoded in {sw.Elapsed.TotalMilliseconds:F2} ms, but CPU software decompression failed.";
                return null;
            }

            Color[] decPixels = UnpackDecodedPixels(decodedRgba, width * height, isHdr);
            var result = ComputeMetrics(refPixels, decPixels, width, height, m_TextureType == TextureType.Normal);
            result.Label = label;
            result.EncodeMs = sw.Elapsed.TotalMilliseconds;
            result.CompressedBytes = raw.Length;
            result.UncompressedBytes = width * height * 4;
            result.DecodedPreview = BuildPreviewTexture(decPixels, width, height, m_ShowAlphaOnly);
            result.HeatmapPreview = BuildHeatmapTexture(refPixels, decPixels, width, height, m_HeatmapGain);
            return result;
        }

        private void ExportFile(string extension)
        {
            if (m_SourceTexture == null)
                return;

            string defaultName = $"{m_SourceTexture.name}_{m_BlockSize}.{extension}";
            string path = EditorUtility.SaveFilePanel($"Export ASTC .{extension}", "", defaultName, extension);
            if (string.IsNullOrEmpty(path))
                return;

            bool isSrgb = m_TextureType == TextureType.Default && GraphicsFormatUtility.IsSRGBFormat(m_SourceTexture.graphicsFormat);
            Texture2D readableSource = CreateReadableCopy(m_SourceTexture, isSrgb);
            try
            {
                var options = new AstcCompressOptions
                {
                    TextureType = m_TextureType,
                    BlockSize = m_BlockSize,
                    Quality = m_Quality,
                    IncludeMipmaps = extension == "ktx",
                    GenerateMipmaps = m_GenerateMipmaps && extension == "ktx",
                    UseAlphaWeight = m_UseAlphaWeight,
                    UsePerceptual = m_UsePerceptual,
                    Backend = m_Mode == BenchmarkMode.CpuOnly ? EncodeBackend.Cpu : EncodeBackend.Auto
                };

                byte[] bytes = extension == "ktx"
                    ? readableSource.EncodeToKTX(options)
                    : readableSource.EncodeToASTC(options);

                if (bytes != null)
                {
                    File.WriteAllBytes(path, bytes);
                    m_StatusMessage = $"Exported {bytes.Length / 1024.0:F1} KB to {path}";
                }
            }
            finally
            {
                DestroyImmediate(readableSource);
            }
        }

        private void DrawResultSection(BenchmarkResult result)
        {
            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                double savings = result.UncompressedBytes > 0
                    ? (1.0 - (double)result.CompressedBytes / result.UncompressedBytes) * 100.0
                    : 0.0;

                EditorGUILayout.LabelField(result.Label, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"Time: {result.EncodeMs:F2} ms   |   Size: {result.CompressedBytes / 1024.0:F1} KB ({savings:F1}% saved)   |   RGB PSNR: {result.RgbPsnrDb:F2} dB   |   RGBA PSNR: {result.RgbaPsnrDb:F2} dB");
                EditorGUILayout.LabelField(
                    $"RGB RMSE: {result.RgbRmse:F4}   |   Alpha RMSE: {result.AlphaRmse:F4}" +
                    (m_TextureType == TextureType.Normal
                        ? $"   |   Normal Error: mean {result.MeanNormalDeg:F2}°, max {result.MaxNormalDeg:F2}°"
                        : string.Empty));

                EditorGUILayout.Space(4f);
                float previewSize = Mathf.Clamp((position.width - 64f) / 3f, 128f, 280f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPreviewColumn("Original", m_ReferencePreview, previewSize);
                    DrawPreviewColumn("Decoded ASTC", result.DecodedPreview, previewSize);
                    DrawPreviewColumn($"Error Heatmap ({m_HeatmapGain:F0}x)", result.HeatmapPreview, previewSize);
                }
            }
        }

        private static void DrawPreviewColumn(string title, Texture2D tex, float size)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(size)))
            {
                EditorGUILayout.LabelField(title, EditorStyles.centeredGreyMiniLabel);
                Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                if (tex != null)
                {
                    EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
                }
            }
        }

        private static Texture2D CreateReadableCopy(Texture2D source, bool srgb)
        {
            var rt = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, !srgb);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply(false, false);
                return copy;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Color[] UnpackDecodedPixels(byte[] rgbaBytes, int pixelCount, bool isHdr)
        {
            var colors = new Color[pixelCount];
            if (isHdr)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    int o = i * 16;
                    colors[i] = new Color(
                        BitConverter.ToSingle(rgbaBytes, o),
                        BitConverter.ToSingle(rgbaBytes, o + 4),
                        BitConverter.ToSingle(rgbaBytes, o + 8),
                        BitConverter.ToSingle(rgbaBytes, o + 12));
                }
            }
            else
            {
                const float inv = 1f / 255f;
                for (int i = 0; i < pixelCount; i++)
                {
                    int o = i * 4;
                    colors[i] = new Color(
                        rgbaBytes[o] * inv,
                        rgbaBytes[o + 1] * inv,
                        rgbaBytes[o + 2] * inv,
                        rgbaBytes[o + 3] * inv);
                }
            }
            return colors;
        }

        private static BenchmarkResult ComputeMetrics(Color[] reference, Color[] decoded, int width, int height, bool isNormalMap)
        {
            int count = Math.Min(reference.Length, decoded.Length);
            double rgbSqErr = 0.0;
            double alphaSqErr = 0.0;
            double normalDegSum = 0.0;
            double normalDegMax = 0.0;

            for (int i = 0; i < count; i++)
            {
                Color a = reference[i];
                Color b = decoded[i];
                double dr = a.r - b.r;
                double dg = a.g - b.g;
                double db = a.b - b.b;
                double da = a.a - b.a;
                rgbSqErr += dr * dr + dg * dg + db * db;
                alphaSqErr += da * da;

                if (isNormalMap)
                {
                    Vector3 na = ReconstructNormal(a.r, a.g);
                    Vector3 nb = ReconstructNormal(b.r, b.g);
                    float dot = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                    double deg = Math.Acos(dot) * (180.0 / Math.PI);
                    normalDegSum += deg;
                    if (deg > normalDegMax)
                        normalDegMax = deg;
                }
            }

            double rgbMse = count > 0 ? rgbSqErr / (count * 3.0) : 0.0;
            double rgbaMse = count > 0 ? (rgbSqErr + alphaSqErr) / (count * 4.0) : 0.0;
            double alphaMse = count > 0 ? alphaSqErr / count : 0.0;

            return new BenchmarkResult
            {
                RgbRmse = Math.Sqrt(rgbMse),
                AlphaRmse = Math.Sqrt(alphaMse),
                RgbPsnrDb = rgbMse > 1e-12 ? 10.0 * Math.Log10(1.0 / rgbMse) : 99.99,
                RgbaPsnrDb = rgbaMse > 1e-12 ? 10.0 * Math.Log10(1.0 / rgbaMse) : 99.99,
                MeanNormalDeg = count > 0 ? normalDegSum / count : 0.0,
                MaxNormalDeg = normalDegMax
            };
        }

        private static Vector3 ReconstructNormal(float r, float g)
        {
            float nx = r * 2f - 1f;
            float ny = g * 2f - 1f;
            float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
            return new Vector3(nx, ny, nz).normalized;
        }

        private static Texture2D BuildPreviewTexture(Color[] pixels, int width, int height, bool alphaOnly)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            if (!alphaOnly)
            {
                tex.SetPixels(pixels);
            }
            else
            {
                var mono = new Color[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    float a = pixels[i].a;
                    mono[i] = new Color(a, a, a, 1f);
                }
                tex.SetPixels(mono);
            }
            tex.Apply(false, false);
            return tex;
        }

        private static Texture2D BuildHeatmapTexture(Color[] reference, Color[] decoded, int width, int height, float gain)
        {
            int count = Math.Min(reference.Length, decoded.Length);
            var heat = new Color[count];
            for (int i = 0; i < count; i++)
            {
                Color a = reference[i];
                Color b = decoded[i];
                float err = (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a)) * 0.25f;
                float t = Mathf.Clamp01(err * gain);
                heat[i] = EvaluateHeatColor(t);
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            tex.SetPixels(heat);
            tex.Apply(false, false);
            return tex;
        }

        private static Color EvaluateHeatColor(float t)
        {
            if (t < 0.25f)
                return Color.Lerp(new Color(0.05f, 0.05f, 0.20f, 1f), new Color(0.0f, 0.65f, 0.85f, 1f), t * 4f);
            if (t < 0.5f)
                return Color.Lerp(new Color(0.0f, 0.65f, 0.85f, 1f), new Color(0.2f, 0.9f, 0.2f, 1f), (t - 0.25f) * 4f);
            if (t < 0.75f)
                return Color.Lerp(new Color(0.2f, 0.9f, 0.2f, 1f), new Color(1.0f, 0.85f, 0.0f, 1f), (t - 0.5f) * 4f);
            return Color.Lerp(new Color(1.0f, 0.85f, 0.0f, 1f), new Color(1.0f, 0.1f, 0.1f, 1f), (t - 0.75f) * 4f);
        }
    }
}
