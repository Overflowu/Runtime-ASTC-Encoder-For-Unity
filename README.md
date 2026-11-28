# Runtime ASTC Encoder for Unity

**English** | [简体中文](README_CN.md)

[![Verify Shaders & Metadata](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml)
[![Build Native Plugins](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml)
![Unity 2021.3+](https://img.shields.io/badge/Unity-2021.3%2B-black?logo=unity)
![UPM v2.3.0](https://img.shields.io/badge/UPM-v2.3.0-blue)
![astc-encoder v5.7.1](https://img.shields.io/badge/astc--encoder-v5.7.1-informational)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](Assets/ASTC%20Encoder/LICENSE.md)

A production-ready **runtime ASTC texture compression package** for Unity (`com.aperture.astcenc`), combining a **real-time GPU Compute Shader encoder** and a **native CPU encoder** (ARM [astc-encoder](https://github.com/ARM-software/astc-encoder) `v5.7.1`) behind a single unified `Encoder` API.

---

## Why Runtime ASTC Encoder?

- **Cut Runtime VRAM by 50–89%**: Compress downloaded UGC, procedural textures, avatars, or `RenderTexture` captures directly into ASTC (`4x4`–`12x12`) on device instead of keeping uncompressed RGBA32 in memory.
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
https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git?path=/Assets/ASTC%20Encoder
```

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
| **iOS** (12.0+) | ARM64 NEON static (`__Internal`) | Metal | CI also builds Device+Simulator `astcenc.xcframework` |
| **Android** (API 21+) | `arm64-v8a` (NEON) & `armeabi-v7a` (NONE) | Vulkan / GLES 3.1+ | `c++_static`, 16 KB page aligned, auto 32/64-bit dispatch |
| **macOS** (11.0+) | Universal (`arm64` NEON + `x86_64` SSE4.1 + `x86_64h` AVX2) | Metal | Editor & Standalone |
| **Windows x64** | **AVX2** + **SSE4.1** (runtime auto-dispatch) | D3D11 / D3D12 | Both DLLs prebuilt in `Plugins/x64/`, static `/MT` |
| **Linux x64** | SSE4.1 (CI artifact) | Vulkan / OpenGL | Built automatically by GitHub Actions |
| **WebGL / Others** | Graceful fallback (`IsCpuSupported == false`) | If Compute supported | Compiles cleanly with no native plugin required |

---

## Performance Benchmarks

<!-- BENCHMARK_START -->
> Automated benchmark results (Apple Silicon Metal GPU vs. CPU + Windows AVX2/SSE4.1 + macOS ARM64 NEON) are generated by [Performance Benchmarks & README Sync](.github/workflows/performance-benchmarks.yml). Run the workflow with `commit_results: true` to populate hardware-verified numbers here and in `docs/Benchmarks.md`.
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
- **[Architecture & Implementation](docs/Architecture.md)** — Dataflow diagram, GPU Compute Shader internals, CPU context pooling & ISA dispatch, and GitHub Actions native build workflow.

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