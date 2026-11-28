using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;
using Encoder = Aperture.Astcenc.Runtime.Encoder;

/// <summary>
/// Standalone Unity Player performance & quality benchmark runner.
/// Activates in built players when launched with <c>-astc-benchmark</c> and writes a structured JSON report
/// to <c>-astc-bench-out &lt;path&gt;</c> (default: <c>astc_benchmark_report.json</c>).
/// Ideal for running real hardware GPU (e.g., macOS Apple Silicon Metal) and CPU benchmarks in CI without Editor overhead.
/// </summary>
public static class AstcPlayerBenchmarkRunner
{
    private const string LogPrefix = "[ASTC_BENCH]";

    [Serializable]
    public sealed class PlayerBenchmarkEntry
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
        public int UncompressedBytes;
        public double RgbPsnrDb;
        public double RgbaPsnrDb;
        public double MeanNormalDeg;
        public string Notes;
    }

    [Serializable]
    public sealed class PlayerBenchmarkReport
    {
        public string SchemaVersion = "1.0";
        public string TimestampUtc;
        public string GitCommit;
        public string UnityVersion;
        public string Platform;
        public string CpuModel;
        public int CpuCores;
        public string GpuModel;
        public string GraphicsDeviceType;
        public bool GpuSupported;
        public bool CpuSupported;
        public bool SupportsDirectCopy4x4;
        public bool SupportsDirectCopy6x6;
        public List<PlayerBenchmarkEntry> Results = new List<PlayerBenchmarkEntry>();
    }

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RunIfRequested()
    {
        if (!HasFlag("-astc-benchmark"))
            return;

        string outPath = GetArgValue("-astc-bench-out", "astc_benchmark_report.json");
        int warmup = GetIntArg("-astc-bench-warmup", 2);
        int iterations = GetIntArg("-astc-bench-iters", 5);
        string gitCommit = GetArgValue("-astc-bench-commit", Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "");

        Debug.Log($"{LogPrefix} Starting standalone benchmark suite: platform={Application.platform}, gpu={SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), cpu={SystemInfo.processorType} ({Environment.ProcessorCount} cores), gpuSupported={Encoder.IsGpuSupported}, cpuSupported={Encoder.IsCpuSupported}");

        try
        {
            PlayerBenchmarkReport report = RunAll(warmup, iterations, gitCommit);
            string fullOutPath = Path.GetFullPath(outPath);
            string dir = Path.GetDirectoryName(fullOutPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullOutPath, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            Debug.Log($"{LogPrefix} ASTC_BENCH_PASSED count={report.Results.Count} output={fullOutPath}");
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} ASTC_BENCH_FAILED: {ex}");
            Application.Quit(1);
        }
    }
#endif

    public static PlayerBenchmarkReport RunAll(int warmup, int iterations, string gitCommit = "")
    {
        var report = new PlayerBenchmarkReport
        {
            TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            GitCommit = gitCommit ?? string.Empty,
            UnityVersion = Application.unityVersion,
            Platform = Application.platform.ToString(),
            CpuModel = SystemInfo.processorType,
            CpuCores = Environment.ProcessorCount,
            GpuModel = SystemInfo.graphicsDeviceName,
            GraphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            GpuSupported = Encoder.IsGpuSupported,
            CpuSupported = Encoder.IsCpuSupported,
            SupportsDirectCopy4x4 = Encoder.SupportsGpuDirectCopy(BlockSize.ASTC_4x4),
            SupportsDirectCopy6x6 = Encoder.SupportsGpuDirectCopy(BlockSize.ASTC_6x6)
        };

        BlockSize[] blockSizes = { BlockSize.ASTC_4x4, BlockSize.ASTC_6x6, BlockSize.ASTC_8x8, BlockSize.ASTC_12x12 };

        if (Encoder.IsGpuSupported)
        {
            foreach (BlockSize bs in blockSizes)
                Encoder.PreloadGpu(bs);
        }

        Texture2D tex512 = CreateProceduralColorTexture(512, 512, srgb: false, withAlpha: true, withMipmaps: false);
        Texture2D tex1024 = CreateProceduralColorTexture(1024, 1024, srgb: false, withAlpha: true, withMipmaps: false);
        Texture2D tex512Srgb = CreateProceduralColorTexture(512, 512, srgb: true, withAlpha: false, withMipmaps: false);
        Texture2D tex512Normal = CreateProceduralNormalTexture(512, 512);
        Texture2D tex512Hdr = CreateProceduralHdrTexture(512, 512);
        Texture2D tex512Mip = CreateProceduralColorTexture(512, 512, srgb: false, withAlpha: true, withMipmaps: true);

        try
        {
            Color32[] ref512 = tex512.GetPixels32(0);
            Color32[] ref1024 = tex1024.GetPixels32(0);
            Color32[] refNormal512 = tex512Normal.GetPixels32(0);

            // 1. GPU vs CPU Side-by-Side Matrix (1024x1024 & 512x512 RGBA8)
            CompressQuality[] q1024 = { CompressQuality.Fast, CompressQuality.Medium };
            foreach (BlockSize bs in blockSizes)
            {
                foreach (CompressQuality q in q1024)
                {
                    if (Encoder.IsGpuSupported)
                    {
                        report.Results.Add(MeasureEncodeWithQuality(
                            name: $"GPU_RGBA8_1024_{bs}_{q}",
                            category: "GPU_vs_CPU_1024",
                            backend: "GPU",
                            tex: tex1024,
                            refPixels: ref1024,
                            bs: bs,
                            q: q,
                            encodeBackend: EncodeBackend.Gpu,
                            warmup: warmup,
                            iterations: iterations));
                    }

                    if (Encoder.IsCpuSupported)
                    {
                        report.Results.Add(MeasureEncodeWithQuality(
                            name: $"CPU_RGBA8_1024_{bs}_{q}",
                            category: "GPU_vs_CPU_1024",
                            backend: "CPU",
                            tex: tex1024,
                            refPixels: ref1024,
                            bs: bs,
                            q: q,
                            encodeBackend: EncodeBackend.Cpu,
                            warmup: Math.Max(1, warmup - 1),
                            iterations: Math.Max(3, iterations - 1)));
                    }
                }
            }

            CompressQuality[] q512 = { CompressQuality.Fast, CompressQuality.Medium, CompressQuality.Thorough };
            foreach (BlockSize bs in blockSizes)
            {
                foreach (CompressQuality q in q512)
                {
                    if (Encoder.IsGpuSupported)
                    {
                        report.Results.Add(MeasureEncodeWithQuality(
                            name: $"GPU_RGBA8_512_{bs}_{q}",
                            category: "GPU_vs_CPU_512",
                            backend: "GPU",
                            tex: tex512,
                            refPixels: ref512,
                            bs: bs,
                            q: q,
                            encodeBackend: EncodeBackend.Gpu,
                            warmup: warmup,
                            iterations: iterations));
                    }

                    if (Encoder.IsCpuSupported)
                    {
                        report.Results.Add(MeasureEncodeWithQuality(
                            name: $"CPU_RGBA8_512_{bs}_{q}",
                            category: "GPU_vs_CPU_512",
                            backend: "CPU",
                            tex: tex512,
                            refPixels: ref512,
                            bs: bs,
                            q: q,
                            encodeBackend: EncodeBackend.Cpu,
                            warmup: Math.Max(1, warmup - 1),
                            iterations: q == CompressQuality.Thorough ? 3 : iterations));
                    }
                }
            }

            // 2. GPU Zero-Readback DirectCopy, MipGen, sRGB, Normal, HDR
            if (Encoder.IsGpuSupported)
            {
                // Zero-readback CompressToTexture (1024x1024 ASTC 6x6 Fast, mip0)
                var direct6x6Opt = AstcCompressOptions.Default;
                direct6x6Opt.Backend = EncodeBackend.Gpu;
                direct6x6Opt.BlockSize = BlockSize.ASTC_6x6;
                direct6x6Opt.Quality = CompressQuality.Fast;
                direct6x6Opt.IncludeMipmaps = false;

                report.Results.Add(Sample(
                    name: "GPU_ZeroReadback_1024_6x6_Fast",
                    category: "GPU_ZeroReadback",
                    backend: "GPU",
                    resolution: "1024x1024",
                    blockSize: "ASTC_6x6",
                    quality: "Fast",
                    totalPixels: 1024L * 1024L,
                    uncompressedBytes: 1024 * 1024 * 4,
                    warmup: warmup,
                    iterations: iterations,
                    action: () =>
                    {
                        Texture2D dst = tex1024.CompressToTexture(direct6x6Opt);
                        int bytes = ExpectedMipChainBytes(1024, 1024, 1, 6);
                        if (dst != null) UnityEngine.Object.DestroyImmediate(dst);
                        return dst != null ? bytes : 0;
                    },
                    notes: Encoder.SupportsGpuDirectCopy(BlockSize.ASTC_6x6) ? "Direct CopyTexture (zero CPU readback)" : "Readback fallback"));

                // Zero-readback CompressToTexture with single-CommandBuffer GPU mipmap generation (1024x1024 + Full Mips)
                var mipGenOpt = AstcCompressOptions.Default;
                mipGenOpt.Backend = EncodeBackend.Gpu;
                mipGenOpt.BlockSize = BlockSize.ASTC_6x6;
                mipGenOpt.Quality = CompressQuality.Fast;
                mipGenOpt.IncludeMipmaps = true;
                mipGenOpt.GenerateMipmaps = true;
                long mip1024Pixels = CalculateMipChainPixels(1024, 1024);

                report.Results.Add(Sample(
                    name: "GPU_ZeroReadback_MipGen_1024_6x6_Fast",
                    category: "GPU_ZeroReadback",
                    backend: "GPU",
                    resolution: "1024x1024+Mips",
                    blockSize: "ASTC_6x6",
                    quality: "Fast",
                    totalPixels: mip1024Pixels,
                    uncompressedBytes: (int)(mip1024Pixels * 4L),
                    warmup: warmup,
                    iterations: iterations,
                    action: () =>
                    {
                        Texture2D dst = tex1024.CompressToTexture(mipGenOpt);
                        int bytes = ExpectedMipChainBytes(1024, 1024, 11, 6);
                        if (dst != null) UnityEngine.Object.DestroyImmediate(dst);
                        return dst != null ? bytes : 0;
                    },
                    notes: "Single-CommandBuffer GPU MipGen + ASTC encode"));

                // Normal Map GPU vs CPU (with MeanNormalDeg)
                report.Results.Add(MeasureNormalWithQuality("GPU_Normal_512_6x6_Medium", tex512Normal, refNormal512, BlockSize.ASTC_6x6, CompressQuality.Medium, EncodeBackend.Gpu, warmup, iterations));
                if (Encoder.IsCpuSupported)
                {
                    report.Results.Add(MeasureNormalWithQuality("CPU_Normal_512_6x6_Medium", tex512Normal, refNormal512, BlockSize.ASTC_6x6, CompressQuality.Medium, EncodeBackend.Cpu, 1, iterations));
                }

                // sRGB & HDR
                var srgbOpt = AstcCompressOptions.Default;
                srgbOpt.Backend = EncodeBackend.Gpu;
                srgbOpt.BlockSize = BlockSize.ASTC_6x6;
                srgbOpt.Quality = CompressQuality.Fast;
                srgbOpt.IncludeMipmaps = false;

                report.Results.Add(Sample(
                    name: "GPU_sRGB_512_6x6_Fast",
                    category: "GPU_Modes",
                    backend: "GPU",
                    resolution: "512x512",
                    blockSize: "ASTC_6x6",
                    quality: "Fast",
                    totalPixels: 512L * 512L,
                    uncompressedBytes: 512 * 512 * 4,
                    warmup: warmup,
                    iterations: iterations,
                    action: () =>
                    {
                        using AstcRaw raw = tex512Srgb.CompressTexture(srgbOpt);
                        return raw != null ? raw.Length : 0;
                    }));

                var hdrOpt = AstcCompressOptions.Default;
                hdrOpt.Backend = EncodeBackend.Gpu;
                hdrOpt.BlockSize = BlockSize.ASTC_6x6;
                hdrOpt.Quality = CompressQuality.Fast;
                hdrOpt.IncludeMipmaps = false;

                report.Results.Add(Sample(
                    name: "GPU_HDR16F_512_6x6_Fast",
                    category: "GPU_Modes",
                    backend: "GPU",
                    resolution: "512x512",
                    blockSize: "ASTC_6x6",
                    quality: "Fast",
                    totalPixels: 512L * 512L,
                    uncompressedBytes: 512 * 512 * 8,
                    warmup: warmup,
                    iterations: iterations,
                    action: () =>
                    {
                        using AstcRaw raw = tex512Hdr.CompressTexture(hdrOpt);
                        return raw != null ? raw.Length : 0;
                    }));
            }

            // 3. CPU RawPixels vs CompressRawPixelsInto (Zero-Alloc) & Decompression
            if (Encoder.IsCpuSupported)
            {
                const int w = 512;
                const int h = 512;
                int outCap = ((w + 5) / 6) * ((h + 5) / 6) * 16;
                var srcNative = new NativeArray<byte>(w * h * 4, Allocator.Persistent);
                var dstNative = new NativeArray<byte>(outCap, Allocator.Persistent);
                try
                {
                    for (int i = 0; i < srcNative.Length; i++)
                        srcNative[i] = (byte)((i * 37) & 0xFF);

                    var rawOpt = AstcCompressOptions.Default;
                    rawOpt.Backend = EncodeBackend.Cpu;
                    rawOpt.BlockSize = BlockSize.ASTC_6x6;
                    rawOpt.Quality = CompressQuality.Fast;
                    rawOpt.ThreadCount = 0;

                    report.Results.Add(Sample(
                        name: "CPU_CompressRawPixels_512_6x6_Fast",
                        category: "CPU_ZeroAlloc",
                        backend: "CPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: w * (long)h,
                        uncompressedBytes: w * h * 4,
                        warmup: 1,
                        iterations: iterations,
                        action: () =>
                        {
                            using AstcRaw raw = Encoder.CompressRawPixels(srcNative, w, h, GraphicsFormat.R8G8B8A8_UNorm, rawOpt);
                            return raw != null ? raw.Length : 0;
                        }));

                    report.Results.Add(Sample(
                        name: "CPU_CompressRawPixelsInto_512_6x6_Fast",
                        category: "CPU_ZeroAlloc",
                        backend: "CPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "Fast",
                        totalPixels: w * (long)h,
                        uncompressedBytes: w * h * 4,
                        warmup: 1,
                        iterations: iterations,
                        action: () =>
                        {
                            bool ok = Encoder.CompressRawPixelsInto(srcNative, dstNative, w, h, GraphicsFormat.R8G8B8A8_UNorm, rawOpt, out int written);
                            return ok ? written : 0;
                        },
                        notes: "Zero-alloc caller-owned NativeArray + AstcContextPool"));
                }
                finally
                {
                    srcNative.Dispose();
                    dstNative.Dispose();
                }

                // CPU Software Decompression (512x512 ASTC_6x6)
                var encOpt = AstcCompressOptions.Default;
                encOpt.Backend = EncodeBackend.Cpu;
                encOpt.BlockSize = BlockSize.ASTC_6x6;
                encOpt.Quality = CompressQuality.Fast;
                encOpt.IncludeMipmaps = false;

                using AstcRaw encoded6x6 = tex512.CompressTexture(encOpt);
                if (encoded6x6 != null)
                {
                    int decThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
                    report.Results.Add(Sample(
                        name: $"CPU_Decompress_512_6x6_{decThreads}T",
                        category: "CPU_Decompress",
                        backend: "CPU",
                        resolution: "512x512",
                        blockSize: "ASTC_6x6",
                        quality: "N/A",
                        totalPixels: w * (long)h,
                        uncompressedBytes: w * h * 4,
                        warmup: 2,
                        iterations: iterations,
                        action: () =>
                        {
                            byte[] rgba = Encoder.Decompress(encoded6x6, w, h, BlockSize.ASTC_6x6, srgb: false, isHdr: false, outputBitness: 8, threadCount: decThreads);
                            return rgba != null ? rgba.Length : 0;
                        },
                        notes: $"Pooled DecompressOnly ({decThreads}T)"));
                }
            }

            // 4. KTX Container & Disk Cache
            {
                var ktxOpt = AstcCompressOptions.Default;
                ktxOpt.Backend = Encoder.IsGpuSupported ? EncodeBackend.Gpu : EncodeBackend.Cpu;
                ktxOpt.BlockSize = BlockSize.ASTC_6x6;
                ktxOpt.Quality = CompressQuality.Fast;
                ktxOpt.IncludeMipmaps = true;

                using AstcRaw mipRaw = tex512Mip.CompressTexture(ktxOpt);
                if (mipRaw != null)
                {
                    int mipCount = tex512Mip.mipmapCount;
                    long mipPixels = CalculateMipChainPixels(512, 512);
                    GraphicsFormat astcFmt = Encoder.GetTargetAstcFormat(tex512Mip.graphicsFormat, BlockSize.ASTC_6x6, TextureType.Default);
                    byte[] ktxBytes = AstcKtxContainer.Encode(mipRaw, 512, 512, mipCount, astcFmt);

                    string tempPath = Path.Combine(Path.GetTempPath(), $"astc_player_bench_{Guid.NewGuid():N}.ktx");
                    try
                    {
                        File.WriteAllBytes(tempPath, ktxBytes);
                        report.Results.Add(Sample(
                            name: "DiskCache_TryLoad_512_6x6_FullMips",
                            category: "Container_Cache",
                            backend: "Disk+GPU",
                            resolution: "512x512+Mips",
                            blockSize: "ASTC_6x6",
                            quality: "N/A",
                            totalPixels: mipPixels,
                            uncompressedBytes: (int)(mipPixels * 4L),
                            warmup: 1,
                            iterations: iterations,
                            action: () =>
                            {
                                Texture2D loaded = AstcDiskCache.TryLoad(tempPath, makeNoLongerReadable: true);
                                int ok = loaded != null ? ktxBytes.Length : 0;
                                if (loaded != null) UnityEngine.Object.DestroyImmediate(loaded);
                                return ok;
                            },
                            notes: "Instant KTX v1 load + GPU upload"));
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                        {
                            try { File.Delete(tempPath); } catch { }
                        }
                    }
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex512);
            UnityEngine.Object.DestroyImmediate(tex1024);
            UnityEngine.Object.DestroyImmediate(tex512Srgb);
            UnityEngine.Object.DestroyImmediate(tex512Normal);
            UnityEngine.Object.DestroyImmediate(tex512Hdr);
            UnityEngine.Object.DestroyImmediate(tex512Mip);
        }

        return report;
    }

    private static PlayerBenchmarkEntry MeasureEncodeWithQuality(
        string name,
        string category,
        string backend,
        Texture2D tex,
        Color32[] refPixels,
        BlockSize bs,
        CompressQuality q,
        EncodeBackend encodeBackend,
        int warmup,
        int iterations)
    {
        int w = tex.width;
        int h = tex.height;
        var opt = AstcCompressOptions.Default;
        opt.Backend = encodeBackend;
        opt.BlockSize = bs;
        opt.Quality = q;
        opt.IncludeMipmaps = false;
        opt.ThreadCount = 0;

        AstcRaw lastRaw = null;
        try
        {
            PlayerBenchmarkEntry entry = Sample(
                name: name,
                category: category,
                backend: backend,
                resolution: $"{w}x{h}",
                blockSize: bs.ToString(),
                quality: q.ToString(),
                totalPixels: (long)w * h,
                uncompressedBytes: w * h * 4,
                warmup: warmup,
                iterations: iterations,
                action: () =>
                {
                    lastRaw?.Dispose();
                    lastRaw = tex.CompressTexture(opt);
                    return lastRaw != null ? lastRaw.Length : 0;
                });

            if (lastRaw != null && Encoder.IsCpuSupported)
            {
                byte[] dec = Encoder.Decompress(lastRaw, w, h, bs, srgb: false, isHdr: false, outputBitness: 8, threadCount: 0);
                if (dec != null)
                {
                    ComputePsnr(refPixels, dec, out entry.RgbPsnrDb, out entry.RgbaPsnrDb);
                    if (entry.RgbPsnrDb < 18.0)
                    {
                        throw new InvalidOperationException($"Benchmark '{name}' produced invalid quality: RGB PSNR={entry.RgbPsnrDb:F2} dB (< 18 dB). Check GPU shader compilation logs.");
                    }
                }
            }

            return entry;
        }
        finally
        {
            lastRaw?.Dispose();
        }
    }

    private static PlayerBenchmarkEntry MeasureNormalWithQuality(
        string name,
        Texture2D normalTex,
        Color32[] refPixels,
        BlockSize bs,
        CompressQuality q,
        EncodeBackend encodeBackend,
        int warmup,
        int iterations)
    {
        int w = normalTex.width;
        int h = normalTex.height;
        var opt = AstcCompressOptions.NormalMap;
        opt.Backend = encodeBackend;
        opt.BlockSize = bs;
        opt.Quality = q;
        opt.IncludeMipmaps = false;
        opt.ThreadCount = 0;

        AstcRaw lastRaw = null;
        try
        {
            PlayerBenchmarkEntry entry = Sample(
                name: name,
                category: "NormalMap",
                backend: encodeBackend == EncodeBackend.Gpu ? "GPU" : "CPU",
                resolution: $"{w}x{h}",
                blockSize: bs.ToString(),
                quality: q.ToString(),
                totalPixels: (long)w * h,
                uncompressedBytes: w * h * 4,
                warmup: warmup,
                iterations: iterations,
                action: () =>
                {
                    lastRaw?.Dispose();
                    lastRaw = normalTex.CompressTexture(opt);
                    return lastRaw != null ? lastRaw.Length : 0;
                });

            if (lastRaw != null && Encoder.IsCpuSupported)
            {
                byte[] dec = Encoder.Decompress(lastRaw, w, h, bs, srgb: false, isHdr: false, outputBitness: 8, threadCount: 0);
                if (dec != null)
                {
                    entry.MeanNormalDeg = ComputeMeanNormalAngularErrorDeg(refPixels, dec);
                    if (entry.MeanNormalDeg > 15.0)
                    {
                        throw new InvalidOperationException($"Benchmark '{name}' produced invalid normal error: {entry.MeanNormalDeg:F2} deg (> 15 deg).");
                    }
                }
            }

            return entry;
        }
        finally
        {
            lastRaw?.Dispose();
        }
    }

    private static PlayerBenchmarkEntry Sample(
        string name,
        string category,
        string backend,
        string resolution,
        string blockSize,
        string quality,
        long totalPixels,
        int uncompressedBytes,
        int warmup,
        int iterations,
        Func<int> action,
        string notes = "")
    {
        int effectiveWarmup = Math.Max(0, warmup);
        int effectiveIters = Math.Max(1, iterations);

        int lastBytes = 0;
        for (int i = 0; i < effectiveWarmup; i++)
            lastBytes = action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        double[] samples = new double[effectiveIters];
        long totalGc = 0;
        var sw = new Stopwatch();
        for (int i = 0; i < effectiveIters; i++)
        {
            long memBefore = GC.GetTotalMemory(false);
            sw.Restart();
            lastBytes = action();
            sw.Stop();
            long memAfter = GC.GetTotalMemory(false);

            samples[i] = sw.Elapsed.TotalMilliseconds;
            if (memAfter > memBefore)
                totalGc += (memAfter - memBefore);
        }

        Array.Sort(samples);
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++) sum += samples[i];

        double minMs = samples[0];
        double maxMs = samples[samples.Length - 1];
        double meanMs = sum / samples.Length;
        double medianMs = (samples.Length & 1) == 1
            ? samples[samples.Length / 2]
            : 0.5 * (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]);
        int p95Idx = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1);
        double p95Ms = samples[Math.Max(0, p95Idx)];
        double mpixPerSec = (medianMs > 1e-6 && totalPixels > 0) ? (totalPixels / 1e6) / (medianMs / 1000.0) : 0.0;

        return new PlayerBenchmarkEntry
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
            GcAllocBytesPerOp = totalGc / effectiveIters,
            OutputBytes = lastBytes,
            UncompressedBytes = uncompressedBytes,
            Notes = notes ?? string.Empty
        };
    }

    private static void ComputePsnr(Color32[] refPixels, byte[] decodedRgba8, out double rgbPsnr, out double rgbaPsnr)
    {
        double rgbSq = 0.0;
        double alphaSq = 0.0;
        int count = Math.Min(refPixels.Length, decodedRgba8.Length / 4);
        for (int i = 0; i < count; i++)
        {
            Color32 r = refPixels[i];
            int o = i * 4;
            double dr = r.r - decodedRgba8[o + 0];
            double dg = r.g - decodedRgba8[o + 1];
            double db = r.b - decodedRgba8[o + 2];
            double da = r.a - decodedRgba8[o + 3];
            rgbSq += dr * dr + dg * dg + db * db;
            alphaSq += da * da;
        }
        double rgbMse = count > 0 ? rgbSq / (count * 3.0) : 0.0;
        double rgbaMse = count > 0 ? (rgbSq + alphaSq) / (count * 4.0) : 0.0;
        rgbPsnr = rgbMse > 1e-12 ? 10.0 * Math.Log10((255.0 * 255.0) / rgbMse) : 99.99;
        rgbaPsnr = rgbaMse > 1e-12 ? 10.0 * Math.Log10((255.0 * 255.0) / rgbaMse) : 99.99;
    }

    private static double ComputeMeanNormalAngularErrorDeg(Color32[] refPixels, byte[] decodedRgba8)
    {
        double degSum = 0.0;
        int count = Math.Min(refPixels.Length, decodedRgba8.Length / 4);
        for (int i = 0; i < count; i++)
        {
            float ax = (refPixels[i].r / 255f) * 2f - 1f;
            float ay = (refPixels[i].g / 255f) * 2f - 1f;
            float az = Mathf.Sqrt(Mathf.Max(0f, 1f - ax * ax - ay * ay));

            // ASTC Normal map stores GGGR (R/G/B = ny, A = nx)
            float bx = (decodedRgba8[i * 4 + 3] / 255f) * 2f - 1f;
            float by = (decodedRgba8[i * 4 + 1] / 255f) * 2f - 1f;
            float bz = Mathf.Sqrt(Mathf.Max(0f, 1f - bx * bx - by * by));

            float dot = Mathf.Clamp(ax * bx + ay * by + az * bz, -1f, 1f);
            degSum += Math.Acos(dot) * (180.0 / Math.PI);
        }
        return count > 0 ? degSum / count : 0.0;
    }

    private static long CalculateMipChainPixels(int width, int height)
    {
        long total = 0;
        int w = width;
        int h = height;
        while (true)
        {
            total += (long)w * h;
            if (w == 1 && h == 1) break;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        return total;
    }

    private static int ExpectedMipChainBytes(int width, int height, int mipCount, int blockDim)
    {
        int total = 0;
        for (int i = 0; i < mipCount; i++)
        {
            int mw = Math.Max(1, width >> i);
            int mh = Math.Max(1, height >> i);
            total += ((mw + blockDim - 1) / blockDim) * ((mh + blockDim - 1) / blockDim) * 16;
        }
        return total;
    }

    private static Texture2D CreateProceduralColorTexture(int width, int height, bool srgb, bool withAlpha, bool withMipmaps)
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
                if (x < width / 4 && y < height / 4)
                {
                    r = 96; g = 144; b = 192; a = 255;
                }
                else
                {
                    float wave = 0.5f + 0.5f * Mathf.Sin((u * 18f + v * 12f) * Mathf.PI);
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

    private static Texture2D CreateProceduralNormalTexture(int width, int height)
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

    private static Texture2D CreateProceduralHdrTexture(int width, int height)
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
                pixels[y * width + x] = new Color(
                    0.1f + u * 2.0f + hotspot * 14.0f,
                    0.05f + v * 1.5f + hotspot * 10.0f,
                    0.2f + (1f - u) * 1.2f + hotspot * 6.0f,
                    1f);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return tex;
    }

    private static bool HasFlag(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string GetArgValue(string name, string defaultValue)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }

    private static int GetIntArg(string name, int defaultValue)
    {
        string raw = GetArgValue(name, null);
        return !string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : defaultValue;
    }
}
