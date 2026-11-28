# Runtime ASTC Encoder for Unity

**English** | [简体中文](README_CN.md)

[![Verify Shaders & Metadata](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml)
[![Unity Multi-Platform E2E](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/unity-multiplatform-e2e.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/unity-multiplatform-e2e.yml)
[![Build Native Plugins](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml)
![Unity 2021.3+](https://img.shields.io/badge/Unity-2021.3%2B-black?logo=unity)
![UPM v2.3.0](https://img.shields.io/badge/UPM-v2.3.0-blue)
![astc-encoder v5.7.1](https://img.shields.io/badge/astc--encoder-v5.7.1-informational)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](Assets/ASTC%20Encoder/LICENSE.md)

A production-ready **runtime ASTC texture compression package** for Unity (`com.aperture.astcenc`), combining a **real-time GPU Compute Shader encoder** and a **native CPU encoder** (ARM [astc-encoder](https://github.com/ARM-software/astc-encoder) `v5.7.1`) behind a single unified `Encoder` API.

---

## Why Runtime ASTC Encoder?

- **Cut Runtime VRAM by 75–97%**: Compress downloaded UGC, procedural textures, avatars, or `RenderTexture` captures directly into ASTC (`4x4`–`12x12`) on device instead of keeping uncompressed RGBA32 in memory.
- **True Zero-Readback GPU Pipeline**: Encode `Texture2D` (including non-readable) and `RenderTexture` — with optional **single-`CommandBuffer` GPU mipmap generation** — straight into hardware ASTC textures via `CommandBuffer.CopyTexture`, including non-multiple dimensions and full mipmap chains.
- **Unified Dual-Backend (`Auto` / `Gpu` / `Cpu`)**: Defaults to `EncodeBackend.Auto`, preferring GPU when supported and falling back seamlessly to pooled native `astc-encoder` on CPU.
- **Built for Production**: Includes a frame-budgeted priority scheduler (`AstcCompressionQueue`), standard KTX v1 container & atomic disk cache (`AstcKtxContainer` / `AstcDiskCache`), zero-allocation raw buffer encoding (`CompressRawPixelsInto`), and an Editor Quality Inspector.

---

## CPU vs. GPU Backend

| Dimension | GPU Backend (`EncodeBackend.Gpu`) | CPU Backend (`EncodeBackend.Cpu`) |
| :--- | :--- | :--- |
| **Best For** | Real-time `RenderTexture`, virtual textures, dynamic atlases, non-readable textures | Offline-grade quality, background UGC compression, software ASTC decompression |
| **Input Requirements** | Any 2D `Texture2D` (no `isReadable` needed) or non-MSAA `RenderTexture` | Readable `Texture2D` (`isReadable == true`) or raw `NativeArray<byte>` / `byte[]` |
| **Block Sizes & Formats** | All 6 (`4x4`–`12x12`), LDR / sRGB / Normal (`GGGR`) / HDR (`CEM 11/14/15`) | All 6 (`4x4`–`12x12`), 24 `GraphicsFormat`s (8/16-bit LDR, 16/32-bit HDR, Normal) |
| **Quality Range** | Real-time `Fast` → `Medium` (Dual-Plane + refit) → `Thorough` (2-Partition) | Full ARM `astc-encoder v5.7.1` range (`Fastest` ~ `Exhaustive`) |
| **CPU Readback** | **Zero readback** via `CopyTexture` (or non-blocking `AsyncGPUReadback` when bytes are requested) | Runs on worker threads / Unity Job System with pooled `AstcContext` |
| **GPU Requirement** | Compute Shaders + `R32G32B32A32_UInt` UAV | None (works everywhere native binaries are present) |

---

## Installation

> **Requirements**: Unity **2021.3+** and **Git LFS** (prebuilt native binaries are stored in LFS).

### Option 1: Unity Package Manager (Recommended)

In Unity, open **Window → Package Manager → `+` → Add package from git URL...**:

```text
https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git?path=/Assets/ASTC%20Encoder#v2.3.0
```

*(Omit `#v2.3.0` to track the latest `master` branch.)*

### Option 2: Local Clone / Direct Copy

```bash
git lfs install
git clone --recurse-submodules https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git
```

Point UPM's **Add package from disk...** to `Assets/ASTC Encoder/package.json`, or copy `Assets/ASTC Encoder/` into your project's `Assets/` folder. Its `Aperture.Astcenc.Runtime.asmdef` isolates `allowUnsafeCode` without affecting your host project.

---

## Quick Start

All public APIs live in `Aperture.Astcenc.Runtime`.

### 1. One-Shot Compression (Auto GPU → CPU Fallback)

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;

public Texture2D CompressTexture(Texture source)
{
    if (!Encoder.IsSupport(source))
        return null;

    // Defaults to EncodeBackend.Auto (zero-readback GPU CopyTexture when supported)
    return source.CompressToTexture(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        includeMipmaps: true);
}
```

### 2. Non-Blocking Async Compression (Mobile Recommended)

```csharp
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using UnityEngine;

public async Task<Texture2D> CompressAsync(Texture source)
{
    return await source.CompressToTextureAsync(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        includeMipmaps: true);
}
```

> For high-volume runtime streaming, use `AstcCompressionQueue` to enforce `MaxGpuTexelsPerFrame`, `MaxConcurrentCpuTasks`, priority scheduling, and single-`CommandBuffer` GPU batching.

### 3. Zero-Readback GPU Encoding + Mipmaps / `CommandBuffer` Atlas Updates

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

public Texture2D CompressRenderTextureWithMips(RenderTexture rt)
{
    // Generates mipmaps and encodes every mip on GPU in a single CommandBuffer (zero CPU readback)
    var options = AstcCompressOptions.Default;
    options.BlockSize = BlockSize.ASTC_6x6;
    options.Quality = CompressQuality.Fast;
    options.GenerateMipmaps = true;
    options.Backend = EncodeBackend.Gpu;

    return rt.CompressToTexture(options);
}

public void UpdateAtlasTile(CommandBuffer cmd, Texture tileSrc, Texture2D astcAtlas, int destX, int destY)
{
    Encoder.Encode(cmd, tileSrc, astcAtlas, TextureType.Default, BlockSize.ASTC_4x4, CompressQuality.Fast,
        sourceMip: 0, destMip: 0, destX: destX, destY: destY);
}
```

---

## Supported Platforms

| Platform | CPU Native (`astc-encoder v5.7.1`) | GPU Compute Backend | Notes |
| :--- | :--- | :--- | :--- |
| **iOS** (12.0+) | ARM64 NEON static (`__Internal`) | Metal | `Plugins/iOS/libastcenc-neon-static.a` prebuilt; CI also builds Device+Simulator `astcenc.xcframework` |
| **Android** (API 21+) | `arm64-v8a` (NEON) & `armeabi-v7a` (NONE) | Vulkan / GLES 3.1+ | `c++_static`, 16 KB page aligned, auto 32/64-bit dispatch |
| **macOS** (11.0+) | Universal (`arm64` NEON + `x86_64` SSE4.1 + `x86_64h` AVX2) | Metal | Editor & Standalone |
| **Windows x64** | **AVX2** + **SSE4.1** (runtime auto-dispatch) | D3D11 / D3D12 | Both DLLs prebuilt in `Plugins/x64/`, static `/MT` |
| **Linux x64** | SSE4.1 (CI artifact / local build script) | Vulkan / OpenGL | Built by GitHub Actions or `.github/scripts/build-linux-astcenc.sh` |
| **WebGL (WASM)** | Static WASM (`__Internal`, optional **WASM SIMD128**) + **Web Worker Pool** | WebGPU / Compute (if supported) | `Plugins/WebGL/` C++ sources compiled by Unity's Emscripten (enable SIMD via **Window → ASTC Encoder → WebGL**); optional non-blocking Web Worker (`Encoder.InitializeWebWorkerAsync()`) with runtime SIMD auto-dispatch & zero COOP/COEP |
| **Other Platforms** | Graceful fallback (`IsCpuSupported == false`) | If Compute supported | Compiles cleanly with no native plugin required |

---

## Performance Benchmarks

<!-- BENCHMARK_START -->
Measured automatically via GitHub Actions on **`Apple Paravirtual device` (Metal) + `Apple M1 (Virtual)` (3 cores) / Unity `2021.3.6f1`** (Release Standalone Player, warmup + multi-iteration median):

#### GPU Compute vs. CPU Encoding (`1024×1024` RGBA8)

| Block Size | Quality | GPU Median (ms) | CPU Median (ms) | GPU Speedup | GPU / CPU PSNR | VRAM Saved |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **ASTC 4x4** | `Fast` | 7.66 | 253.41 | **33.1×** | 48.9 / 49.0 dB | -75.0% |
| **ASTC 4x4** | `Medium` | 41.00 | 519.59 | **12.7×** | 46.4 / 50.2 dB | -75.0% |
| **ASTC 6x6** | `Fast` | 20.34 | 371.75 | **18.3×** | 29.3 / 41.7 dB | -88.8% |
| **ASTC 6x6** | `Medium` | 57.49 | 876.38 | **15.2×** | 32.6 / 43.2 dB | -88.8% |
| **ASTC 8x8** | `Fast` | 8.51 | 836.19 | **98.2×** | 43.4 / 46.0 dB | -93.8% |
| **ASTC 8x8** | `Medium` | 37.02 | 1848.60 | **49.9×** | 44.1 / 47.9 dB | -93.8% |
| **ASTC 12x12** | `Fast` | 10.85 | 710.89 | **65.5×** | 24.7 / 27.8 dB | -97.2% |
| **ASTC 12x12** | `Medium` | 30.73 | 1254.79 | **40.8×** | 25.3 / 30.1 dB | -97.2% |

#### Key Runtime Highlights

| Scenario | Median / Throughput | GC Alloc | Notes |
| :--- | :--- | ---: | :--- |
| GPU Zero-Readback `CompressToTexture` (`1024×1024 6x6 Fast`) | **2.70 ms** (388.7 MP/s) | 0.0 KB | Direct `CopyTexture`, zero CPU readback |
| Single-`CommandBuffer` GPU MipGen + Encode (`1024×1024+Mips`) | **0.75 ms** (1861.7 MP/s) | 0.0 KB | Full 11-mip chain generated & encoded on GPU |
| Normal Map GGGR Encoding (`512×512 6x6 Medium`) | GPU **19.26 ms** vs CPU 319.21 ms | 0.0 KB | Mean angular error: GPU 0.27° vs CPU 0.15° |
| Zero-Alloc `CompressRawPixelsInto` (`512×512 6x6 Fast`) | **196.09 ms** (1.3 MP/s) | **4096 B** | Caller-owned `NativeArray` + `AstcContextPool` |
| KTX Disk Cache `AstcDiskCache.TryLoad` (`512×512+Mips`) | **0.66 ms** | 124.8 KB | Compress once, instant load on subsequent launches |

#### Cross-Platform Native CPU SIMD (`astc-encoder v5.7.1`, `512×512` RGBA8 `ASTC_6x6`)

| Platform / CPU | ISA | Threads | `Fast` (ms) | `Medium` (ms) | `Fast` MP/s | Decompress (ms) |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `AVX2` | 4 | 40.04 | 173.18 | 6.55 | 1.28 |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `SSE4.1` | 4 | 43.90 | 180.55 | 5.97 | 1.87 |
| Darwin arm64 (`Apple M1 (Virtual)`) | `ARM64_NEON` | 3 | 34.68 | 154.93 | 7.56 | 1.51 |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `SSE4.1` | 4 | 50.48 | 197.06 | 5.19 | 2.18 |

> Provenance: Commit `d3223a5` · 2026-11-28 · [Full Benchmark Matrix & Regression Baseline (docs/Benchmarks.md)](docs/Benchmarks.md) · [Actions Run](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/runs/46153027417)
<!-- BENCHMARK_END -->

**Run Benchmarks Locally in Unity** (exports JSON / CSV / Markdown to `Logs/AstcBenchmarks/`):
- **Editor Menu**: **Window → ASTC Encoder → Run Performance Benchmark Suite** (or **Run Full Suite** in Benchmark & Inspector)
- **Test Runner**: EditMode `[Category("Performance")]` (`AstcPerformanceBenchmarks`)
- **CLI / Player**: `Unity -batchmode -projectPath . -executeMethod Aperture.Astcenc.Editor.AstcPerformanceBenchmarkRunner.RunAllFromCli -quit` or launch a built Player with `-astc-benchmark -astc-bench-out report.json`

---

## Editor Tools & Samples

- **Benchmark & Quality Inspector**: Open **Window → ASTC Encoder → Benchmark & Inspector** to compare CPU vs. GPU outputs side-by-side with decoded previews, amplified error heatmaps, PSNR / RMSE / Normal angular error, memory savings, and `.ktx` / `.astc` export.
- **EditMode Tests**: Golden cross-decoding NUnit tests are included under `Aperture.Astcenc.Tests`.
- **Sample Scene**: Open `Assets/Scenes/SampleScene.unity` (`Assets/Scripts/Sample.cs`) to switch `EncodeBackend` (`Auto` / `Gpu` / `Cpu`) directly in the Inspector.

---

## Documentation

- **[API Reference](docs/API.md)** — Full `Encoder`, `AstcContext`, `AstcCompressionQueue`, `AstcKtxContainer`, `AstcDiskCache`, `AstcRaw`, 24-format mapping table, and advanced code samples (zero-alloc raw pixels, KTX caching, `.astc` export).
- **[Architecture & Implementation](docs/Architecture.md)** — Dataflow diagram, GPU Compute Shader internals, CPU context pooling & ISA dispatch, and GitHub Actions CI workflows.
- **[Performance Benchmarks](docs/Benchmarks.md)** — Full hardware-verified GPU/CPU matrix and regression baseline (`docs/benchmarks/baseline.json`).
- **[Multi-Platform CI & E2E Guide](docs/CI_E2E_GUIDE.md)** — Setup and execution guide for GameCI EditMode tests and 5-platform end-to-end builds.
- **[Changelog](Assets/ASTC%20Encoder/CHANGELOG.md)** — Version release history for `com.aperture.astcenc`.

---

## Key Best Practices

- **Dispose Unmanaged Buffers**: Always wrap `AstcRaw` and manually created `AstcContext` instances in `using`. Static CPU entry points automatically pool native contexts (call `Encoder.ReleaseCpuContextPool()` if you need to reclaim pool memory immediately).
- **Warm Up GPU Shaders**: Call `Encoder.PreloadGpu(blockSize)` during loading screens to avoid first-frame shader hitching, and `Encoder.ReleaseGpuCache()` when idle.
- **Alpha Weighting**: Enable `useAlphaWeight: true` for transparent UI/sprites/foliage; keep it `false` (default) for channel-packed maps (ORM / MRAO / masks).
- **Desktop Non-ASTC GPUs**: On Windows D3D11/D3D12 GPUs without hardware ASTC sampling, the GPU backend still exports raw bytes / `.astc` / `.ktx`; in the Unity Editor, `CompressToTexture` automatically uses software preview fallback.
- **Y-Axis Orientation**: Both backends encode in Unity's bottom-up `Texture2D` orientation (`y = 0` at bottom) with no extra flip needed for cameras or `Graphics.Blit`.

---

## License & Acknowledgments

- Licensed under the **MIT License** — see [LICENSE.md](Assets/ASTC%20Encoder/LICENSE.md).
- CPU backend powered by [ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder) under the **Apache License 2.0** — see [Third Party Notices.md](Assets/ASTC%20Encoder/Third%20Party%20Notices.md).