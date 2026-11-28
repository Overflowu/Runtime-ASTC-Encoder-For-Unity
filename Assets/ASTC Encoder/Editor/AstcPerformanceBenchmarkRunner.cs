using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;
using Encoder = Aperture.Astcenc.Runtime.Encoder;

namespace Aperture.Astcenc.Editor
{
    [Serializable]
    public sealed class AstcBenchmarkMetrics
    {
        public string Name;
        public string Category;
        public string Backend;
        public string Resolution;
        public string BlockSize;
        public string Quality;
        public int WarmupRuns;
        public int Iterations;
        public double MinMs;
        public double MedianMs;
        public double MeanMs;
        public double P95Ms;
        public double MaxMs;
        public double ThroughputMPixPerSec;
        public long GcAllocBytesPerOp;
        public int OutputBytes;
        public string Notes;
    }

    [Serializable]
    public sealed class AstcBenchmarkReport
    {
        public string TimestampUtc;
        public string UnityVersion;
        public string Platform;
        public string CpuModel;
        public int CpuCores;
        public string GpuModel;
        public string GraphicsDeviceType;
        public bool GpuSupported;
        public bool CpuSupported;
        public List<AstcBenchmarkMetrics> Results = new List<AstcBenchmarkMetrics>();
    }

    /// <summary>
    /// Statistical micro-benchmark sampler with warmup, percentile calculation (Min/Median/Mean/P95/Max),
    /// throughput (MP/s), and managed GC allocation tracking.
    /// </summary>
    public static class AstcBenchmarkSampler
    {
        public static AstcBenchmarkMetrics Measure(
            string name,
            string category,
            string backend,
            string resolution,
            string blockSize,
            string quality,
            long totalPixels,
            int warmupRuns,
            int iterations,
            Func<int> action,
            string notes = "")
        {
            int effectiveWarmup = Math.Max(0, warmupRuns);
            int effectiveIters = Math.Max(1, iterations);

            int lastBytes = 0;
            for (int w = 0; w < effectiveWarmup; w++)
            {
                lastBytes = action();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            double[] samplesMs = new double[effectiveIters];
            long totalGcDelta = 0;
            var sw = new Stopwatch();

            for (int i = 0; i < effectiveIters; i++)
            {
                long memBefore = GC.GetTotalMemory(false);
                sw.Restart();
                lastBytes = action();
                sw.Stop();
                long memAfter = GC.GetTotalMemory(false);

                samplesMs[i] = sw.Elapsed.TotalMilliseconds;
                if (memAfter > memBefore)
                {
                    totalGcDelta += (memAfter - memBefore);
                }
            }

            Array.Sort(samplesMs);
            double sum = 0.0;
            for (int i = 0; i < samplesMs.Length; i++)
                sum += samplesMs[i];

            double minMs = samplesMs[0];
            double maxMs = samplesMs[samplesMs.Length - 1];
            double meanMs = sum / samplesMs.Length;
            double medianMs = (samplesMs.Length & 1) == 1
                ? samplesMs[samplesMs.Length / 2]
                : 0.5 * (samplesMs[samplesMs.Length / 2 - 1] + samplesMs[samplesMs.Length / 2]);

            int p95Index = Math.Min(samplesMs.Length - 1, (int)Math.Ceiling(samplesMs.Length * 0.95) - 1);
            double p95Ms = samplesMs[Math.Max(0, p95Index)];

            double mpixPerSec = (medianMs > 1e-6 && totalPixels > 0)
                ? (totalPixels / 1e6) / (medianMs / 1000.0)
                : 0.0;

            return new AstcBenchmarkMetrics
            {
                Name = name,
                Category = category,
                Backend = backend,
                Resolution = resolution,
                BlockSize = blockSize,
                Quality = quality,
                WarmupRuns = effectiveWarmup,
                Iterations = effectiveIters,
                MinMs = minMs,
                MedianMs = medianMs,
                MeanMs = meanMs,
                P95Ms = p95Ms,
                MaxMs = maxMs,
                ThroughputMPixPerSec = mpixPerSec,
                GcAllocBytesPerOp = totalGcDelta / effectiveIters,
                OutputBytes = lastBytes,
                Notes = notes ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Automated performance benchmark suite for Runtime-ASTC-Encoder-For-Unity.
    /// Runnable from Editor MenuItem, Unity Test Runner (AstcPerformanceBenchmarks), or CLI batchmode (-executeMethod).
    /// </summary>
    public static class AstcPerformanceBenchmarkRunner
    {
        public const string DefaultOutputDirectory = "Logs/AstcBenchmarks";

        [MenuItem("Window/ASTC Encoder/Run Performance Benchmark Suite")]
        public static void RunFromMenu()
        {
            AstcBenchmarkReport report = RunFullSuite(warmup: 2, iterations: 5, exportReports: true, out string exportDir);
            Debug.Log($"[ASTC Benchmark] Completed {report.Results.Count} benchmarks. Reports saved to: {exportDir}\n{FormatMarkdownTable(report)}");
        }

        /// <summary>
        /// Unity batchmode entrypoint:
        /// Unity -batchmode -projectPath . -executeMethod Aperture.Astcenc.Editor.AstcPerformanceBenchmarkRunner.RunAllFromCli -quit
        /// </summary>
        public static void RunAllFromCli()
        {
            AstcBenchmarkReport report = RunFullSuite(warmup: 2, iterations: 5, exportReports: true, out string exportDir);
            Debug.Log($"[ASTC Benchmark CLI] Completed {report.Results.Count} benchmarks. Output: {exportDir}\n{FormatMarkdownTable(report)}");
        }

        public static AstcBenchmarkReport CreateEmptyReport()
        {
            return new AstcBenchmarkReport
            {
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                UnityVersion = Application.unityVersion,
                Platform = Application.platform.ToString(),
                CpuModel = SystemInfo.processorType,
                CpuCores = Environment.ProcessorCount,
                GpuModel = SystemInfo.graphicsDeviceName,
                GraphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                GpuSupported = Encoder.IsGpuSupported,
                CpuSupported = Encoder.IsCpuSupported
            };
        }

        public static AstcBenchmarkReport RunFullSuite(int warmup, int iterations, bool exportReports, out string exportedDirectory)
        {
            exportedDirectory = null;
            AstcBenchmarkReport report = CreateEmptyReport();

            if (Encoder.IsGpuSupported)
            {
                report.Results.AddRange(RunGpuEncodingSuite(warmup, iterations));
            }
            if (Encoder.IsCpuSupported)
            {
                report.Results.AddRange(RunCpuEncodingSuite(Math.Max(1, warmup), iterations));
                report.Results.AddRange(RunCpuRawPixelsSuite(Math.Max(1, warmup), iterations));
                report.Results.AddRange(RunDecompressionSuite(Math.Max(1, warmup), iterations));
            }

            report.Results.AddRange(RunContainerAndCacheSuite(Math.Max(1, warmup), iterations));
            report.Results.AddRange(RunQueueBatchSuite(1, Math.Max(2, iterations / 2)));

            if (exportReports)
            {
                exportedDirectory = ExportReport(report, DefaultOutputDirectory);
            }

            return report;
        }

        public static List<AstcBenchmarkMetrics> RunGpuEncodingSuite(int warmup = 2, int iterations = 5)
        {
            var results = new List<AstcBenchmarkMetrics>();
            if (!Encoder.IsGpuSupported)
                return results;

            BlockSize[] blockSizes = { BlockSize.ASTC_4x4, BlockSize.ASTC_6x6, BlockSize.ASTC_8x8, BlockSize.ASTC_12x12 };
            foreach (BlockSize bs in blockSizes)
            {
                Encoder.PreloadGpu(bs);
            }

            // 1. 512x512 RGBA8 matrix (Fast / Medium / Thorough across 4x4, 6x6, 8x8, 12x12)
            Texture2D tex512 = CreateProceduralColorTexture(512, 512, srgb: false, withAlpha: true, withMipmaps: false);
            Texture2D tex1024 = CreateProceduralColorTexture(1024, 1024, srgb: false, withAlpha: true, withMipmaps: false);
            Texture2D tex512Srgb = CreateProceduralColorTexture(512, 512, srgb: true, withAlpha: false, withMipmaps: false);
            Texture2D tex512Normal = CreateProceduralNormalTexture(512, 512);
            Texture2D tex512Hdr = CreateProceduralHdrTexture(512, 512);

            try
            {
                CompressQuality[] qualities512 = { CompressQuality.Fast, CompressQuality.Medium, CompressQuality.Thorough };
                foreach (BlockSize bs in blockSizes)
                {
                    foreach (CompressQuality q in qualities512)
                    {
                        var opt = AstcCompressOptions.Default;
                        opt.Backend = EncodeBackend.Gpu;
                        opt.BlockSize = bs;
                        opt.Quality = q;
                        opt.IncludeMipmaps = false;

                        results.Add(AstcBenchmarkSampler.Measure(
                            name: $"GPU_RGBA8_512_{bs}_{q}",
                            category: "GPU_Encode",
                            backend: "GPU",
                            resolution: "512x512",
                            blockSize: bs.ToString(),
                            quality: q.ToString(),
                            totalPixels: 512L * 512L,
                            warmupRuns: warmup,
                            iterations: iterations,
                            action: () =>
                            {
                                using AstcRaw raw = tex512.CompressTexture(opt);
                                return raw != null ? raw.Length : 0;
                            }));
                    }
                }

                // 2. 1024x1024 RGBA8 (Fast / Medium across 4x4, 6x6, 8x8, 12x12)
                CompressQuality[] qualities1024 = { CompressQuality.Fast, CompressQuality.Medium };
                foreach (BlockSize bs in blockSizes)
                {
                    foreach (CompressQuality q in qualities1024)
                    {
                        var opt = AstcCompressOptions.Default;
                        opt.Backend = EncodeBackend.Gpu;
                        opt.BlockSize = bs;
                        opt.Quality = q;
                        opt.IncludeMipmaps = false;

                        results.Add(AstcBenchmarkSampler.Measure(
                            name: $"GPU_RGBA8_1024_{bs}_{q}",
                            category: "GPU_Encode",
                            backend: "GPU",
                            resolution: "1024x1024",
                            blockSize: bs.ToString(),
                            quality: q.ToString(),
                            totalPixels: 1024L * 1024L,
                            warmupRuns: warmup,
                            iterations: iterations,
                            action: () =>
                            {
                                using AstcRaw raw = tex1024.CompressTexture(opt);
                                return raw != null ? raw.Length : 0;
                            }));
                    }
                }

                // 3. sRGB / Normal / HDR / GPU Mipmap Generation at 512x512 ASTC_6x6
                {
                    var srgbOpt = AstcCompressOptions.Default;
                    srgbOpt.Backend = EncodeBackend.Gpu;
                    srgbOpt.BlockSize = BlockSize.ASTC_6x6;
                    srgbOpt.Quality = CompressQuality.Fast;
                    srgbOpt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_sRGB_512_6x6_Fast",
                        category: "GPU_Modes",
                        backend: "GPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = tex512Srgb.CompressTexture(srgbOpt);
                            return raw != null ? raw.Length : 0;
                        }));

                    var normOpt = AstcCompressOptions.NormalMap;
                    normOpt.Backend = EncodeBackend.Gpu;
                    normOpt.BlockSize = BlockSize.ASTC_6x6;
                    normOpt.Quality = CompressQuality.Medium;
                    normOpt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_Normal_512_6x6_Medium",
                        category: "GPU_Modes",
                        backend: "GPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Medium",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = tex512Normal.CompressTexture(normOpt);
                            return raw != null ? raw.Length : 0;
                        }));

                    var hdrOpt = AstcCompressOptions.Default;
                    hdrOpt.Backend = EncodeBackend.Gpu;
                    hdrOpt.BlockSize = BlockSize.ASTC_6x6;
                    hdrOpt.Quality = CompressQuality.Fast;
                    hdrOpt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_HDR16F_512_6x6_Fast",
                        category: "GPU_Modes",
                        backend: "GPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = tex512Hdr.CompressTexture(hdrOpt);
                            return raw != null ? raw.Length : 0;
                        }));

                    var mipOpt = AstcCompressOptions.Default;
                    mipOpt.Backend = EncodeBackend.Gpu;
                    mipOpt.BlockSize = BlockSize.ASTC_6x6;
                    mipOpt.Quality = CompressQuality.Fast;
                    mipOpt.IncludeMipmaps = true;
                    mipOpt.GenerateMipmaps = true;

                    long mipPixels = CalculateMipChainPixels(512, 512);
                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_MipGen_RGBA8_512_6x6_Fast",
                        category: "GPU_Modes",
                        backend: "GPU",
                        resolution: "512x512+Mips",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: mipPixels,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = tex512.CompressTexture(mipOpt);
                            return raw != null ? raw.Length : 0;
                        },
                        notes: "GPU mipmap generation + encode"));

                    // Direct CopyTexture vs Readback fallback for CompressToTexture (aligned 4x4 mip0 vs non-aligned 6x6 mip0)
                    var direct4x4Opt = AstcCompressOptions.Default;
                    direct4x4Opt.Backend = EncodeBackend.Gpu;
                    direct4x4Opt.BlockSize = BlockSize.ASTC_4x4;
                    direct4x4Opt.Quality = CompressQuality.Fast;
                    direct4x4Opt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_CompressToTexture_512_4x4_Mip0",
                        category: "GPU_DirectCopy",
                        backend: "GPU",
                        resolution: "512x512",
                        blockSize: "ASTC_4x4",
                        quality: "Fast",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            Texture2D outTex = tex512.CompressToTexture(direct4x4Opt);
                            int ok = outTex != null ? 1 : 0;
                            if (outTex != null) UnityEngine.Object.DestroyImmediate(outTex);
                            return ok;
                        },
                        notes: Encoder.SupportsGpuDirectCopy(BlockSize.ASTC_4x4) ? "DirectCopy eligible" : "Readback fallback (no HW ASTC)"));

                    var fallback6x6Opt = AstcCompressOptions.Default;
                    fallback6x6Opt.Backend = EncodeBackend.Gpu;
                    fallback6x6Opt.BlockSize = BlockSize.ASTC_6x6;
                    fallback6x6Opt.Quality = CompressQuality.Fast;
                    fallback6x6Opt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "GPU_CompressToTexture_512_6x6_Mip0",
                        category: "GPU_DirectCopy",
                        backend: "GPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            Texture2D outTex = tex512.CompressToTexture(fallback6x6Opt);
                            int ok = outTex != null ? 1 : 0;
                            if (outTex != null) UnityEngine.Object.DestroyImmediate(outTex);
                            return ok;
                        },
                        notes: "512%6!=0 -> Readback fallback"));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex512);
                UnityEngine.Object.DestroyImmediate(tex1024);
                UnityEngine.Object.DestroyImmediate(tex512Srgb);
                UnityEngine.Object.DestroyImmediate(tex512Normal);
                UnityEngine.Object.DestroyImmediate(tex512Hdr);
            }

            return results;
        }

        public static List<AstcBenchmarkMetrics> RunCpuEncodingSuite(int warmup = 1, int iterations = 5)
        {
            var results = new List<AstcBenchmarkMetrics>();
            if (!Encoder.IsCpuSupported)
                return results;

            Texture2D tex512 = CreateProceduralColorTexture(512, 512, srgb: false, withAlpha: true, withMipmaps: false);
            try
            {
                BlockSize[] blockSizes = { BlockSize.ASTC_4x4, BlockSize.ASTC_6x6, BlockSize.ASTC_8x8, BlockSize.ASTC_12x12 };
                CompressQuality[] qualities = { CompressQuality.Fast, CompressQuality.Medium };
                int autoThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

                // 1. Cold (per-call AstcContext.Create) vs Warm (reused AstcContext)
                foreach (BlockSize bs in blockSizes)
                {
                    foreach (CompressQuality q in qualities)
                    {
                        var coldOpt = AstcCompressOptions.Default;
                        coldOpt.Backend = EncodeBackend.Cpu;
                        coldOpt.BlockSize = bs;
                        coldOpt.Quality = q;
                        coldOpt.IncludeMipmaps = false;
                        coldOpt.ThreadCount = autoThreads;

                        results.Add(AstcBenchmarkSampler.Measure(
                            name: $"CPU_Cold_512_{bs}_{q}_{autoThreads}T",
                            category: "CPU_ColdVsWarm",
                            backend: "CPU",
                            resolution: "512x512",
                            blockSize: bs.ToString(),
                            quality: q.ToString(),
                            totalPixels: 512L * 512L,
                            warmupRuns: warmup,
                            iterations: iterations,
                            action: () =>
                            {
                                using AstcRaw raw = tex512.CompressTexture(coldOpt);
                                return raw != null ? raw.Length : 0;
                            },
                            notes: "Includes AstcContext.Create/Dispose per call"));

                        using AstcContext warmCtx = AstcContext.Create(
                            tex512.graphicsFormat,
                            TextureType.Default,
                            bs,
                            q,
                            threadCount: autoThreads);

                        if (warmCtx != null)
                        {
                            results.Add(AstcBenchmarkSampler.Measure(
                                name: $"CPU_Warm_512_{bs}_{q}_{autoThreads}T",
                                category: "CPU_ColdVsWarm",
                                backend: "CPU",
                                resolution: "512x512",
                                blockSize: bs.ToString(),
                                quality: q.ToString(),
                                totalPixels: 512L * 512L,
                                warmupRuns: warmup,
                                iterations: iterations,
                                action: () =>
                                {
                                    using AstcRaw raw = warmCtx.CompressTexture(tex512, TextureType.Default);
                                    return raw != null ? raw.Length : 0;
                                },
                                notes: "Reused AstcContext (steady-state)"));
                        }
                    }
                }

                // 2. Threading scaling at 512x512 ASTC_6x6 Fast (1T, 4T/Auto, MaxCores, Unity JobSystem)
                int[] threadCounts = { 1, autoThreads, Environment.ProcessorCount };
                foreach (int tc in threadCounts)
                {
                    using AstcContext ctx = AstcContext.Create(
                        tex512.graphicsFormat,
                        TextureType.Default,
                        BlockSize.ASTC_6x6,
                        CompressQuality.Fast,
                        threadCount: tc,
                        useUnityJobSystem: false);

                    if (ctx != null)
                    {
                        results.Add(AstcBenchmarkSampler.Measure(
                            name: $"CPU_ThreadPool_{tc}T_512_6x6_Fast",
                            category: "CPU_Threading",
                            backend: "CPU",
                            resolution: "512x512",
                            blockSize: "ASTC_6x6",
                            quality: "Fast",
                            totalPixels: 512L * 512L,
                            warmupRuns: warmup,
                            iterations: iterations,
                            action: () =>
                            {
                                using AstcRaw raw = ctx.CompressTexture(tex512, TextureType.Default);
                                return raw != null ? raw.Length : 0;
                            },
                            notes: $".NET Parallel.For ({tc} threads)"));
                    }
                }

                using AstcContext jobCtx = AstcContext.Create(
                    tex512.graphicsFormat,
                    TextureType.Default,
                    BlockSize.ASTC_6x6,
                    CompressQuality.Fast,
                    threadCount: autoThreads,
                    useUnityJobSystem: true);

                if (jobCtx != null)
                {
                    results.Add(AstcBenchmarkSampler.Measure(
                        name: $"CPU_JobSystem_{autoThreads}T_512_6x6_Fast",
                        category: "CPU_Threading",
                        backend: "CPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: 512L * 512L,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = jobCtx.CompressTexture(tex512, TextureType.Default);
                            return raw != null ? raw.Length : 0;
                        },
                        notes: $"Unity IJobParallelFor ({autoThreads} threads)"));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex512);
            }

            return results;
        }

        public static List<AstcBenchmarkMetrics> RunCpuRawPixelsSuite(int warmup = 1, int iterations = 5)
        {
            var results = new List<AstcBenchmarkMetrics>();
            if (!Encoder.IsCpuSupported)
                return results;

            const int w = 512;
            const int h = 512;
            int blocksX = (w + 5) / 6;
            int blocksY = (h + 5) / 6;
            int outCapacity = blocksX * blocksY * 16;

            using var srcRgba8 = new NativeArray<byte>(w * h * 4, Allocator.Persistent);
            using var dstAstc = new NativeArray<byte>(outCapacity, Allocator.Persistent);
            var srcWritable = srcRgba8;
            for (int i = 0; i < srcWritable.Length; i++)
                srcWritable[i] = (byte)((i * 37) & 0xFF);

            var opt = AstcCompressOptions.Default;
            opt.Backend = EncodeBackend.Cpu;
            opt.BlockSize = BlockSize.ASTC_6x6;
            opt.Quality = CompressQuality.Fast;
            opt.ThreadCount = 0;

            results.Add(AstcBenchmarkSampler.Measure(
                name: "CPU_CompressRawPixels_512_6x6_Fast",
                category: "CPU_RawPixels",
                backend: "CPU",
                resolution: "512x512",
                blockSize: "ASTC_6x6",
                quality: "Fast",
                totalPixels: w * (long)h,
                warmupRuns: warmup,
                iterations: iterations,
                action: () =>
                {
                    using AstcRaw raw = Encoder.CompressRawPixels(srcRgba8, w, h, GraphicsFormat.R8G8B8A8_UNorm, opt);
                    return raw != null ? raw.Length : 0;
                },
                notes: "Allocates AstcRaw per call"));

            results.Add(AstcBenchmarkSampler.Measure(
                name: "CPU_CompressRawPixelsInto_512_6x6_Fast",
                category: "CPU_RawPixels",
                backend: "CPU",
                resolution: "512x512",
                blockSize: "ASTC_6x6",
                quality: "Fast",
                totalPixels: w * (long)h,
                warmupRuns: warmup,
                iterations: iterations,
                action: () =>
                {
                    bool ok = Encoder.CompressRawPixelsInto(srcRgba8, dstAstc, w, h, GraphicsFormat.R8G8B8A8_UNorm, opt, out int written);
                    return ok ? written : 0;
                },
                notes: "Caller-owned destination NativeArray"));

            return results;
        }

        public static List<AstcBenchmarkMetrics> RunDecompressionSuite(int warmup = 1, int iterations = 5)
        {
            var results = new List<AstcBenchmarkMetrics>();
            if (!Encoder.IsCpuSupported)
                return results;

            const int w = 512;
            const int h = 512;
            Texture2D tex512 = CreateProceduralColorTexture(w, h, srgb: false, withAlpha: true, withMipmaps: false);
            try
            {
                BlockSize[] blockSizes = { BlockSize.ASTC_4x4, BlockSize.ASTC_6x6, BlockSize.ASTC_8x8, BlockSize.ASTC_12x12 };
                int multiThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

                foreach (BlockSize bs in blockSizes)
                {
                    var opt = AstcCompressOptions.Default;
                    opt.Backend = EncodeBackend.Cpu;
                    opt.BlockSize = bs;
                    opt.Quality = CompressQuality.Fast;
                    opt.IncludeMipmaps = false;

                    using AstcRaw encoded = tex512.CompressTexture(opt);
                    if (encoded == null)
                        continue;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: $"CPU_Decompress_1T_512_{bs}",
                        category: "CPU_Decompress",
                        backend: "CPU",
                        resolution: "512x512",
                        blockSize: bs.ToString(),
                        quality: "N/A",
                        totalPixels: w * (long)h,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            byte[] rgba = Encoder.Decompress(encoded, w, h, bs, srgb: false, isHdr: false, outputBitness: 8, threadCount: 1);
                            return rgba != null ? rgba.Length : 0;
                        },
                        notes: "1 thread (includes CreateDecompressor)"));

                    if (multiThreads > 1)
                    {
                        results.Add(AstcBenchmarkSampler.Measure(
                            name: $"CPU_Decompress_{multiThreads}T_512_{bs}",
                            category: "CPU_Decompress",
                            backend: "CPU",
                            resolution: "512x512",
                            blockSize: bs.ToString(),
                            quality: "N/A",
                            totalPixels: w * (long)h,
                            warmupRuns: warmup,
                            iterations: iterations,
                            action: () =>
                            {
                                byte[] rgba = Encoder.Decompress(encoded, w, h, bs, srgb: false, isHdr: false, outputBitness: 8, threadCount: multiThreads);
                                return rgba != null ? rgba.Length : 0;
                            },
                            notes: $"{multiThreads} threads (includes CreateDecompressor)"));
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex512);
            }

            return results;
        }

        public static List<AstcBenchmarkMetrics> RunContainerAndCacheSuite(int warmup = 1, int iterations = 5)
        {
            var results = new List<AstcBenchmarkMetrics>();
            const int w = 512;
            const int h = 512;
            long mipPixels = CalculateMipChainPixels(w, h);

            Texture2D tex512Mip = CreateProceduralColorTexture(w, h, srgb: false, withAlpha: true, withMipmaps: true);
            string tempKtxPath = Path.Combine(Path.GetTempPath(), $"astc_bench_{Guid.NewGuid():N}.ktx");

            try
            {
                var opt = AstcCompressOptions.Default;
                opt.Backend = Encoder.IsGpuSupported ? EncodeBackend.Gpu : EncodeBackend.Cpu;
                opt.BlockSize = BlockSize.ASTC_6x6;
                opt.Quality = CompressQuality.Fast;
                opt.IncludeMipmaps = true;

                using AstcRaw raw = tex512Mip.CompressTexture(opt);
                if (raw == null)
                    return results;

                int mipCount = tex512Mip.mipmapCount;
                GraphicsFormat astcFmt = Encoder.GetTargetAstcFormat(tex512Mip.graphicsFormat, opt.BlockSize, opt.TextureType);

                byte[] ktxBytes = null;
                results.Add(AstcBenchmarkSampler.Measure(
                    name: "KTX_Encode_512_6x6_FullMips",
                    category: "Container_Cache",
                    backend: "Managed",
                    resolution: "512x512+Mips",
                    blockSize: "ASTC_6x6",
                    quality: "N/A",
                    totalPixels: mipPixels,
                    warmupRuns: warmup,
                    iterations: iterations,
                    action: () =>
                    {
                        ktxBytes = AstcKtxContainer.Encode(raw, w, h, mipCount, astcFmt);
                        return ktxBytes != null ? ktxBytes.Length : 0;
                    }));

                if (ktxBytes != null)
                {
                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "KTX_TryDecode_512_6x6_FullMips",
                        category: "Container_Cache",
                        backend: "Managed",
                        resolution: "512x512+Mips",
                        blockSize: "ASTC_6x6",
                        quality: "N/A",
                        totalPixels: mipPixels,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            bool ok = AstcKtxContainer.TryDecode(ktxBytes, out KtxAstcData decoded);
                            return ok && decoded.RawAstcPayload != null ? decoded.RawAstcPayload.Length : 0;
                        },
                        notes: "Watch GcAllocBytesPerOp for LOH payload copy"));

                    File.WriteAllBytes(tempKtxPath, ktxBytes);
                    results.Add(AstcBenchmarkSampler.Measure(
                        name: "DiskCache_TryLoad_512_6x6_FullMips",
                        category: "Container_Cache",
                        backend: "Disk+GPU",
                        resolution: "512x512+Mips",
                        blockSize: "ASTC_6x6",
                        quality: "N/A",
                        totalPixels: mipPixels,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            Texture2D loaded = AstcDiskCache.TryLoad(tempKtxPath, makeNoLongerReadable: true);
                            int ok = loaded != null ? ktxBytes.Length : 0;
                            if (loaded != null) UnityEngine.Object.DestroyImmediate(loaded);
                            return ok;
                        }));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex512Mip);
                if (File.Exists(tempKtxPath))
                {
                    try { File.Delete(tempKtxPath); } catch { }
                }
            }

            return results;
        }

        public static List<AstcBenchmarkMetrics> RunQueueBatchSuite(int warmup = 1, int iterations = 3)
        {
            var results = new List<AstcBenchmarkMetrics>();
            const int batchCount = 8;
            const int size = 256;
            long totalPixels = (long)batchCount * size * size;

            var textures = new Texture2D[batchCount];
            for (int i = 0; i < batchCount; i++)
                textures[i] = CreateProceduralColorTexture(size, size, srgb: false, withAlpha: true, withMipmaps: false, seedOffset: i * 13);

            try
            {
                if (Encoder.IsCpuSupported)
                {
                    using var cpuQueue = new AstcCompressionQueue
                    {
                        MaxConcurrentCpuTasks = Math.Max(1, Environment.ProcessorCount / 2)
                    };

                    var cpuOpt = AstcCompressOptions.Default;
                    cpuOpt.Backend = EncodeBackend.Cpu;
                    cpuOpt.BlockSize = BlockSize.ASTC_6x6;
                    cpuOpt.Quality = CompressQuality.Fast;
                    cpuOpt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: $"Queue_CPU_Burst_{batchCount}x{size}_6x6_Fast",
                        category: "Queue_Batch",
                        backend: "CPU_Queue",
                        resolution: $"{batchCount}x{size}x{size}",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: totalPixels,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            var tasks = new Task<AstcRaw>[batchCount];
                            for (int i = 0; i < batchCount; i++)
                                tasks[i] = cpuQueue.EnqueueToRaw(textures[i], cpuOpt);

                            while (cpuQueue.PendingCpuCount > 0 || cpuQueue.ActiveCpuCount > 0)
                            {
                                cpuQueue.ProcessFrame();
                                System.Threading.Thread.Sleep(1);
                            }
                            Task.WaitAll(tasks);

                            int totalBytes = 0;
                            for (int i = 0; i < batchCount; i++)
                            {
                                using AstcRaw r = tasks[i].Result;
                                if (r != null) totalBytes += r.Length;
                            }
                            return totalBytes;
                        },
                        notes: "Pooled AstcContext with parent table sharing"));
                }

                if (Encoder.IsGpuSupported)
                {
                    Encoder.PreloadGpu(BlockSize.ASTC_4x4);
                    using var gpuQueue = new AstcCompressionQueue
                    {
                        MaxGpuTexelsPerFrame = totalPixels
                    };

                    var gpuOpt = AstcCompressOptions.Default;
                    gpuOpt.Backend = EncodeBackend.Gpu;
                    gpuOpt.BlockSize = BlockSize.ASTC_4x4;
                    gpuOpt.Quality = CompressQuality.Fast;
                    gpuOpt.IncludeMipmaps = false;

                    results.Add(AstcBenchmarkSampler.Measure(
                        name: $"Queue_GPU_Burst_{batchCount}x{size}_4x4_Fast",
                        category: "Queue_Batch",
                        backend: "GPU_Queue",
                        resolution: $"{batchCount}x{size}x{size}",
                        blockSize: "ASTC_4x4",
                        quality: "Fast",
                        totalPixels: totalPixels,
                        warmupRuns: warmup,
                        iterations: iterations,
                        action: () =>
                        {
                            var tasks = new Task<Texture2D>[batchCount];
                            for (int i = 0; i < batchCount; i++)
                                tasks[i] = gpuQueue.EnqueueToTexture(textures[i], gpuOpt);

                            gpuQueue.ProcessFrame();

                            // Flush pending AsyncGPUReadback if fallback path was taken on non-ASTC desktop Editor.
                            int guard = 0;
                            while (!Task.WhenAll(tasks).IsCompleted && guard < 2000)
                            {
                                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
                                System.Threading.Thread.Sleep(1);
                                guard++;
                            }

                            int created = 0;
                            for (int i = 0; i < batchCount; i++)
                            {
                                if (tasks[i].Status == TaskStatus.RanToCompletion && tasks[i].Result != null)
                                {
                                    created++;
                                    UnityEngine.Object.DestroyImmediate(tasks[i].Result);
                                }
                            }
                            return created;
                        },
                        notes: "Single-frame CommandBuffer batch when DirectCopy supported"));
                }
            }
            finally
            {
                for (int i = 0; i < batchCount; i++)
                    UnityEngine.Object.DestroyImmediate(textures[i]);
            }

            return results;
        }

        public static string ExportReport(AstcBenchmarkReport report, string outputDirectory = DefaultOutputDirectory)
        {
            string fullDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullDir);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string jsonPath = Path.Combine(fullDir, $"astc_benchmark_{stamp}.json");
            string csvPath = Path.Combine(fullDir, $"astc_benchmark_{stamp}.csv");
            string mdPath = Path.Combine(fullDir, $"astc_benchmark_{stamp}.md");

            File.WriteAllText(jsonPath, JsonUtility.ToJson(report, true), Encoding.UTF8);
            File.WriteAllText(csvPath, FormatCsv(report), Encoding.UTF8);
            File.WriteAllText(mdPath, FormatMarkdownTable(report), Encoding.UTF8);

            return fullDir;
        }

        public static string FormatMarkdownTable(AstcBenchmarkReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# ASTC Performance Benchmark ({report.TimestampUtc})");
            sb.AppendLine($"- **Unity**: `{report.UnityVersion}` | **Platform**: `{report.Platform}`");
            sb.AppendLine($"- **CPU**: `{report.CpuModel}` ({report.CpuCores} cores) | **GPU**: `{report.GpuModel}` (`{report.GraphicsDeviceType}`)");
            sb.AppendLine();
            sb.AppendLine("| Benchmark | Backend | Resolution | Block | Quality | Min (ms) | Median (ms) | P95 (ms) | MP/s | GC Alloc (KB) | Notes |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | :--- |");

            foreach (AstcBenchmarkMetrics r in report.Results)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3} | {4} | {5:F2} | {6:F2} | {7:F2} | {8:F2} | {9:F1} | {10} |",
                    r.Name, r.Backend, r.Resolution, r.BlockSize, r.Quality,
                    r.MinMs, r.MedianMs, r.P95Ms, r.ThroughputMPixPerSec,
                    r.GcAllocBytesPerOp / 1024.0, r.Notes));
            }
            return sb.ToString();
        }

        public static string FormatCsv(AstcBenchmarkReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Category,Backend,Resolution,BlockSize,Quality,WarmupRuns,Iterations,MinMs,MedianMs,MeanMs,P95Ms,MaxMs,ThroughputMPixPerSec,GcAllocBytesPerOp,OutputBytes,Notes");
            foreach (AstcBenchmarkMetrics r in report.Results)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8:F4},{9:F4},{10:F4},{11:F4},{12:F4},{13:F4},{14},{15},\"{16}\"",
                    r.Name, r.Category, r.Backend, r.Resolution, r.BlockSize, r.Quality,
                    r.WarmupRuns, r.Iterations, r.MinMs, r.MedianMs, r.MeanMs, r.P95Ms, r.MaxMs,
                    r.ThroughputMPixPerSec, r.GcAllocBytesPerOp, r.OutputBytes,
                    (r.Notes ?? string.Empty).Replace("\"", "\"\"")));
            }
            return sb.ToString();
        }

        public static long CalculateMipChainPixels(int width, int height)
        {
            long total = 0;
            int w = width;
            int h = height;
            while (true)
            {
                total += (long)w * h;
                if (w == 1 && h == 1)
                    break;
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
            }
            return total;
        }

        /// <summary>
        /// Deterministic procedural RGBA texture mixing flat blocks (Void-Extent), smooth gradients,
        /// high-frequency edges, and alpha variation to exercise all encoder paths realistically.
        /// </summary>
        public static Texture2D CreateProceduralColorTexture(int width, int height, bool srgb, bool withAlpha, bool withMipmaps, int seedOffset = 0)
        {
            GraphicsFormat fmt = srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            TextureCreationFlags flags = withMipmaps ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var tex = new Texture2D(width, height, fmt, flags);

            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = height > 1 ? (float)y / (height - 1) : 0f;
                for (int x = 0; x < width; x++)
                {
                    float u = width > 1 ? (float)x / (width - 1) : 0f;
                    byte r, g, b, a;

                    // Bottom-left quadrant: flat constant color (exercises Void-Extent fast path)
                    if (x < width / 4 && y < height / 4)
                    {
                        r = 96;
                        g = 144;
                        b = 192;
                        a = 255;
                    }
                    else
                    {
                        float wave = 0.5f + 0.5f * Mathf.Sin((u * 18f + v * 12f + seedOffset) * Mathf.PI);
                        float checker = (((x >> 3) + (y >> 3)) & 1) == 0 ? 0.15f : -0.15f;
                        r = (byte)Mathf.Clamp(Mathf.RoundToInt((u * 0.7f + wave * 0.3f + checker) * 255f), 0, 255);
                        g = (byte)Mathf.Clamp(Mathf.RoundToInt((v * 0.7f + (1f - wave) * 0.3f - checker) * 255f), 0, 255);
                        b = (byte)Mathf.Clamp(Mathf.RoundToInt(((u + v) * 0.4f + wave * 0.2f) * 255f), 0, 255);
                        a = withAlpha
                            ? (byte)Mathf.Clamp(Mathf.RoundToInt((0.2f + 0.8f * Mathf.Abs(Mathf.Sin((u - v) * 4f * Mathf.PI))) * 255f), 0, 255)
                            : (byte)255;
                    }

                    pixels[y * width + x] = new Color32(r, g, b, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: withMipmaps, makeNoLongerReadable: false);
            return tex;
        }

        public static Texture2D CreateProceduralNormalTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = height > 1 ? (float)y / (height - 1) : 0f;
                for (int x = 0; x < width; x++)
                {
                    float u = width > 1 ? (float)x / (width - 1) : 0f;
                    float nx = Mathf.Sin(u * 16f * Mathf.PI) * 0.45f + (u - 0.5f) * 0.5f;
                    float ny = Mathf.Cos(v * 16f * Mathf.PI) * 0.45f + (v - 0.5f) * 0.5f;
                    float nz = Mathf.Sqrt(Mathf.Max(0.05f, 1f - nx * nx - ny * ny));
                    Vector3 n = new Vector3(nx, ny, nz).normalized;

                    pixels[y * width + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255),
                        255);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        public static Texture2D CreateProceduralHdrTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = height > 1 ? (float)y / (height - 1) : 0f;
                for (int x = 0; x < width; x++)
                {
                    float u = width > 1 ? (float)x / (width - 1) : 0f;
                    float hotspot = Mathf.Exp(-((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 24f);
                    float r = 0.1f + u * 2.0f + hotspot * 14.0f;
                    float g = 0.05f + v * 1.5f + hotspot * 10.0f;
                    float b = 0.2f + (1f - u) * 1.2f + hotspot * 6.0f;
                    pixels[y * width + x] = new Color(r, g, b, 1f);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }
    }
}
