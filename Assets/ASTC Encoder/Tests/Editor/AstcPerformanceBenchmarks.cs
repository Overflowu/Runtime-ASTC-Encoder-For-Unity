using System.Collections.Generic;
using Aperture.Astcenc.Editor;
using Aperture.Astcenc.Runtime;
using NUnit.Framework;

namespace Aperture.Astcenc.Tests
{
    /// <summary>
    /// NUnit EditMode performance benchmark suite for Runtime-ASTC-Encoder-For-Unity.
    /// Marked [Explicit] + [Category("Performance")] so regular unit-test runs stay fast.
    /// Run from Unity Test Runner (select Performance category) or CLI:
    ///   Unity -batchmode -runTests -testPlatform EditMode -testCategory Performance -quit
    /// </summary>
    [TestFixture]
    [Category("Performance")]
    [Explicit("Performance benchmarks — run explicitly or via -testCategory Performance.")]
    public class AstcPerformanceBenchmarks
    {
        private const int k_WarmupRuns = 2;
        private const int k_Iterations = 5;

        [Test]
        public void Benchmark_Gpu_EncodingMatrix()
        {
            if (!Encoder.IsGpuSupported)
                Assert.Ignore("GPU compute ASTC backend is not supported on this device.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunGpuEncodingSuite(k_WarmupRuns, k_Iterations);
            report.Results.AddRange(results);

            Assert.Greater(results.Count, 0);
            foreach (AstcBenchmarkMetrics r in results)
            {
                Assert.Greater(r.OutputBytes, 0, $"Benchmark {r.Name} produced empty output.");
                Assert.Greater(r.MedianMs, 0.0, $"Benchmark {r.Name} recorded non-positive duration.");
            }

            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_Cpu_EncodingMatrix_ColdVsWarm_AndThreading()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("Native CPU astcenc library is not available on this host.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunCpuEncodingSuite(1, k_Iterations);
            report.Results.AddRange(results);

            Assert.Greater(results.Count, 0);
            foreach (AstcBenchmarkMetrics r in results)
            {
                Assert.Greater(r.OutputBytes, 0, $"Benchmark {r.Name} produced empty output.");
                Assert.Greater(r.MedianMs, 0.0, $"Benchmark {r.Name} recorded non-positive duration.");
            }

            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_Cpu_RawPixels_Vs_CompressRawPixelsInto()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("Native CPU astcenc library is not available on this host.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunCpuRawPixelsSuite(1, k_Iterations);
            report.Results.AddRange(results);

            Assert.AreEqual(2, results.Count);
            foreach (AstcBenchmarkMetrics r in results)
            {
                Assert.Greater(r.OutputBytes, 0, $"Benchmark {r.Name} produced empty output.");
            }

            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_Cpu_Decompression()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("Native CPU astcenc library is not available on this host.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunDecompressionSuite(1, k_Iterations);
            report.Results.AddRange(results);

            Assert.Greater(results.Count, 0);
            foreach (AstcBenchmarkMetrics r in results)
            {
                Assert.Greater(r.OutputBytes, 0, $"Benchmark {r.Name} produced empty output.");
            }

            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_KtxContainer_And_DiskCache()
        {
            if (!Encoder.IsGpuSupported && !Encoder.IsCpuSupported)
                Assert.Ignore("No ASTC compression backend is available on this host.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunContainerAndCacheSuite(1, k_Iterations);
            report.Results.AddRange(results);

            Assert.Greater(results.Count, 0);
            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_CompressionQueue_Burst()
        {
            if (!Encoder.IsGpuSupported && !Encoder.IsCpuSupported)
                Assert.Ignore("No ASTC compression backend is available on this host.");

            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.CreateEmptyReport();
            List<AstcBenchmarkMetrics> results = AstcPerformanceBenchmarkRunner.RunQueueBatchSuite(1, 3);
            report.Results.AddRange(results);

            Assert.Greater(results.Count, 0);
            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }

        [Test]
        public void Benchmark_FullSuite_ExportReport()
        {
            AstcBenchmarkReport report = AstcPerformanceBenchmarkRunner.RunFullSuite(
                warmup: k_WarmupRuns,
                iterations: k_Iterations,
                exportReports: true,
                out string exportDir);

            Assert.Greater(report.Results.Count, 0);
            TestContext.Out.WriteLine($"Exported benchmark reports to: {exportDir}");
            TestContext.Out.WriteLine(AstcPerformanceBenchmarkRunner.FormatMarkdownTable(report));
        }
    }
}
