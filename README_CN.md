# Runtime ASTC Encoder for Unity

[English](README.md) | **简体中文**

[![Verify Shaders & Metadata](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/verify-shaders-and-code.yml)
[![Build Native Plugins](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml/badge.svg)](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/workflows/build-native-plugins.yml)
![Unity 2021.3+](https://img.shields.io/badge/Unity-2021.3%2B-black?logo=unity)
![UPM v2.3.0](https://img.shields.io/badge/UPM-v2.3.0-blue)
![astc-encoder v5.7.1](https://img.shields.io/badge/astc--encoder-v5.7.1-informational)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](Assets/ASTC%20Encoder/LICENSE.md)

面向 Unity 的**生产级运行时 ASTC 纹理压缩包**（`com.aperture.astcenc`），在统一的 `Encoder` API 下同时集成**实时 GPU Compute Shader 编码器**与**原生 CPU 编码器**（ARM [astc-encoder](https://github.com/ARM-software/astc-encoder) `v5.7.1`）。

---

## 为什么选择 Runtime ASTC Encoder？

- **运行时显存直降 50%–89%**：在端侧将下载的 UGC 图片、程序化贴图、捏脸头像或 `RenderTexture` 截图实时压为 ASTC（`4x4`–`12x12`），告别未压缩 RGBA32 显存膨胀。
- **真正的 GPU 零回读管线**：支持非可读 `Texture2D` 与 `RenderTexture`，可选**单条 `CommandBuffer` 内一体化 GPU Mipmap 生成 + ASTC 编码**，通过 `CommandBuffer.CopyTexture` 直拷至硬件 ASTC 纹理（兼容非整除尺寸与完整 Mipmap 链）。
- **双后端统一切换（`Auto` / `Gpu` / `Cpu`）**：默认 `EncodeBackend.Auto`，设备支持时优先走 GPU，不支持时无缝回退至带上下文池化的原生 CPU 编码器。
- **开箱即用的生产配套**：内置帧预算优先级调度队列（`AstcCompressionQueue`）、标准 KTX v1 容器与原子磁盘缓存（`AstcKtxContainer` / `AstcDiskCache`）、零分配原始像素直压（`CompressRawPixelsInto`）以及 Editor 质检窗口。

---

## CPU 与 GPU 后端选型

| 维度 | GPU 后端（`EncodeBackend.Gpu`） | CPU 后端（`EncodeBackend.Cpu`） |
| :--- | :--- | :--- |
| **最适用场景** | 实时 `RenderTexture`、虚拟纹理、动态图集更新、非可读贴图 | 离线级高画质压缩、后台线程 UGC 压缩、CPU 软件解码 |
| **输入要求** | 任意 2D `Texture2D`（无需 `isReadable`）或非 MSAA `RenderTexture` | 可读 `Texture2D`（`isReadable == true`）或原始 `NativeArray<byte>` / `byte[]` |
| **块尺寸与格式** | 全部 6 种（`4x4`–`12x12`），LDR / sRGB / Normal（`GGGR`）/ HDR（`CEM 11/14/15`） | 全部 6 种（`4x4`–`12x12`），24 种 `GraphicsFormat`（8/16-bit LDR、16/32-bit HDR、法线） |
| **画质档位** | 实时 `Fast` → `Medium`（Dual-Plane + 端点重拟合）→ `Thorough`（2-Partition） | 完整 ARM `astc-encoder v5.7.1` 档位（`Fastest` ~ `Exhaustive`） |
| **CPU 回读与并发** | `CopyTexture` **零回读**（需字节数据时走非阻塞 `AsyncGPUReadback`） | 后台线程池 / Unity Job System，静态接口自动池化复用 `AstcContext` |
| **GPU 依赖** | Compute Shader + `R32G32B32A32_UInt` UAV | 无（凡提供原生库的平台均可运行） |

---

## 安装

> **环境要求**：Unity **2021.3+**，且需启用 **Git LFS**（预编译原生库由 LFS 管理）。

### 方式一：Unity Package Manager（推荐）

在 Unity 中打开 **Window → Package Manager → `+` → Add package from git URL...**：

```text
https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git?path=/Assets/ASTC%20Encoder#v2.3.0
```

*（去掉 `#v2.3.0` 可直接跟踪 `master` 分支最新提交。）*

### 方式二：本地克隆 / 直接拷贝

```bash
git lfs install
git clone --recurse-submodules https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git
```

在 UPM 中选择 **Add package from disk...** 指向 `Assets/ASTC Encoder/package.json`，或直接将 `Assets/ASTC Encoder/` 拷贝到工程的 `Assets/` 目录。包内 `Aperture.Astcenc.Runtime.asmdef` 独立启用 `allowUnsafeCode`，不影响宿主工程配置。

---

## 快速上手

所有公开 API 位于 `Aperture.Astcenc.Runtime` 命名空间。

### 1. 一行代码压缩（默认 Auto：优先 GPU，自动回退 CPU）

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;

public Texture2D CompressTexture(Texture source)
{
    if (!Encoder.IsSupport(source))
        return null;

    // 默认 EncodeBackend.Auto（移动端支持硬件 ASTC 时走 GPU 零回读 CopyTexture）
    return source.CompressToTexture(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        includeMipmaps: true);
}
```

### 2. 非阻塞异步压缩（移动端推荐）

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

> 若存在大批量运行时流式加载，推荐使用 `AstcCompressionQueue`，支持 `MaxGpuTexelsPerFrame` 每帧像素预算、`MaxConcurrentCpuTasks` 并发上限、优先级调度与单帧单条 `CommandBuffer` GPU 合批。

### 3. GPU 零回读压缩 + Mipmap 生成 / `CommandBuffer` 图集更新

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

public Texture2D CompressRenderTextureWithMips(RenderTexture rt)
{
    // 单条 CommandBuffer 内完成 GPU Mipmap 生成 + 全级 ASTC 编码（全程零 CPU 回读）
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

## 平台支持矩阵

| 平台 | CPU 原生后端（`astc-encoder v5.7.1`） | GPU Compute 后端 | 说明 |
| :--- | :--- | :--- | :--- |
| **iOS**（12.0+） | ARM64 NEON 静态库（`__Internal`） | Metal | CI 同步产出真机+模拟器 `astcenc.xcframework` |
| **Android**（API 21+） | `arm64-v8a`（NEON）与 `armeabi-v7a`（NONE） | Vulkan / GLES 3.1+ | `c++_static`、16 KB 页对齐、运行时自动 32/64 位分派 |
| **macOS**（11.0+） | Universal（`arm64` NEON + `x86_64` SSE4.1 + `x86_64h` AVX2） | Metal | Editor & Standalone |
| **Windows x64** | **AVX2** + **SSE4.1**（运行时自动分派） | D3D11 / D3D12 | 双 DLL 已内置于 `Plugins/x64/`，静态 `/MT` 运行时 |
| **Linux x64** | SSE4.1（CI 自动构建产物） | Vulkan / OpenGL | 由 GitHub Actions 自动构建 |
| **WebGL / 其他** | 平滑降级（`IsCpuSupported == false`） | 支持 Compute 时可用 | 无需原生库即可正常编译 |

---

## 性能基准测试

<!-- BENCHMARK_START -->
以下数据由 GitHub Actions 在 **`Apple Paravirtual device`（Metal）+ `Apple M1 (Virtual)`（3 核） / Unity `2021.3.6f1`** 上自动实测生成（无 Editor 开销的 Standalone Player，预热 + 多次采样中位数）：

#### GPU Compute vs. CPU 编码对比（`1024×1024` RGBA8）

| 块尺寸 | 质量 | GPU 耗时 (ms) | CPU 耗时 (ms) | GPU 加速比 | GPU / CPU PSNR | 显存节省 |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **ASTC 4x4** | `Fast` | 7.66 | 253.41 | **33.1×** | 48.9 / 49.0 dB | -75.0% |
| **ASTC 4x4** | `Medium` | 41.00 | 519.59 | **12.7×** | 46.4 / 50.2 dB | -75.0% |
| **ASTC 6x6** | `Fast` | 20.34 | 371.75 | **18.3×** | 29.3 / 41.7 dB | -88.8% |
| **ASTC 6x6** | `Medium` | 57.49 | 876.38 | **15.2×** | 32.6 / 43.2 dB | -88.8% |
| **ASTC 8x8** | `Fast` | 8.51 | 836.19 | **98.2×** | 43.4 / 46.0 dB | -93.8% |
| **ASTC 8x8** | `Medium` | 37.02 | 1848.60 | **49.9×** | 44.1 / 47.9 dB | -93.8% |
| **ASTC 12x12** | `Fast` | 10.85 | 710.89 | **65.5×** | 24.7 / 27.8 dB | -97.2% |
| **ASTC 12x12** | `Medium` | 30.73 | 1254.79 | **40.8×** | 25.3 / 30.1 dB | -97.2% |

#### 关键运行时特性实测

| 场景 | 中位数耗时 / 吞吐 | GC 分配 | 说明 |
| :--- | :--- | ---: | :--- |
| GPU 零回读 `CompressToTexture` (`1024×1024 6x6 Fast`) | **2.70 ms** (388.7 MP/s) | 0.0 KB | 直拷硬件 ASTC 纹理，无 CPU 回读 |
| GPU 单 CommandBuffer MipGen + 编码 (`1024×1024+Mips`) | **0.75 ms** (1861.7 MP/s) | 0.0 KB | 11 级 Mip 生成与编码一体化完成 |
| 法线贴图 GGGR 编码 (`512×512 6x6 Medium`) | GPU **19.26 ms** vs CPU 319.21 ms | 0.0 KB | 平均角度误差：GPU 0.27° vs CPU 0.15° |
| 原始像素直压 `CompressRawPixelsInto` (`512×512 6x6 Fast`) | **196.09 ms** (1.3 MP/s) | **4096 B** | 复⽤ NativeArray + `AstcContextPool` |
| KTX 磁盘缓存秒开 `AstcDiskCache.TryLoad` (`512×512+Mips`) | **0.66 ms** | 124.8 KB | 一次压缩，后续启动直传 GPU |

#### 跨平台原生 CPU 指令集实测（`astc-encoder v5.7.1`，`512×512` RGBA8 `ASTC_6x6`）

| 平台 / CPU | 指令集（ISA） | 线程 | `Fast` (ms) | `Medium` (ms) | `Fast` 吞吐 | CPU 软解 (ms) |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `AVX2` | 4 | 40.04 | 173.18 | 6.55 | 1.28 |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `SSE4.1` | 4 | 43.90 | 180.55 | 5.97 | 1.87 |
| Darwin arm64 (`Apple M1 (Virtual)`) | `ARM64_NEON` | 3 | 34.68 | 154.93 | 7.56 | 1.51 |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `SSE4.1` | 4 | 50.48 | 197.06 | 5.19 | 2.18 |

> 基准溯源：Commit `d3223a5` · 2026-11-28 · [查看完整全矩阵与基线对比（docs/Benchmarks.md）](docs/Benchmarks.md) · [Actions Run](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/runs/46153027417)
<!-- BENCHMARK_END -->

**本地在 Unity 中运行完整基准套件**（自动导出 JSON / CSV / Markdown 至 `Logs/AstcBenchmarks/`）：
- **Editor 菜单**：**Window → ASTC Encoder → Run Performance Benchmark Suite**（或在 Benchmark & Inspector 窗口点击 **Run Full Suite**）
- **Test Runner**：运行 EditMode `Performance` 分类（`AstcPerformanceBenchmarks`）
- **CLI / Player**：`Unity -batchmode -projectPath . -executeMethod Aperture.Astcenc.Editor.AstcPerformanceBenchmarkRunner.RunAllFromCli -quit`，或对编译出的 Player 传入 `-astc-benchmark -astc-bench-out report.json`

---

## Editor 工具与示例

- **可视化对比与质检窗口**：打开 **Window → ASTC Encoder → Benchmark & Inspector**，同屏对比 CPU 与 GPU 解码结果、放大误差热力图、PSNR / RMSE / 法线角度误差与显存节省率，并支持导出 `.ktx` / `.astc`。
- **单元测试**：内置 EditMode 交叉解码黄金测试套件（`Aperture.Astcenc.Tests`）。
- **示例场景**：打开 `Assets/Scenes/SampleScene.unity`（`Assets/Scripts/Sample.cs`），可直接在 Inspector 中切换 `EncodeBackend`（`Auto` / `Gpu` / `Cpu`）。

---

## 进阶文档

- **[完整 API 参考（docs/API.md）](docs/API.md)** — `Encoder`、`AstcContext`、`AstcCompressionQueue`、`AstcKtxContainer`、`AstcDiskCache`、`AstcRaw`、24 种输入格式映射表，以及零分配原始像素直压、KTX 磁盘缓存、`.astc` 导出示例。
- **[架构与实现细节（docs/Architecture.md）](docs/Architecture.md)** — 数据流架构图、GPU Compute Shader 算法与寄存器优化、CPU 上下文池化与指令集分派，以及 GitHub Actions 跨平台原生库构建流程。

---

## 使用注意事项

- **非托管资源释放**：`AstcRaw` 与手动创建的 `AstcContext` 请务必配合 `using` 或 `Dispose()` 释放；静态 CPU 接口已自动通过 `AstcContextPool` 复用原生上下文（如需立即回收池内存可调用 `Encoder.ReleaseCpuContextPool()`）。
- **GPU Shader 预热**：建议在加载界面调用 `Encoder.PreloadGpu(blockSize)` 避免首帧编译卡顿，空闲时可调用 `Encoder.ReleaseGpuCache()` 释放引用。
- **Alpha 权重开关**：透明 UI / 精灵 / 植被贴图建议传 `useAlphaWeight: true`；通道打包贴图（ORM / MRAO / Mask）请保持默认 `false`。
- **桌面非 ASTC 显卡行为**：Windows D3D11/D3D12 等不支持硬件 ASTC 采样的显卡仍可通过 GPU 后端导出原始字节 / `.astc` / `.ktx`；在 Unity Editor 下 `CompressToTexture` 会自动走软解预览回退。
- **Y 轴方向约定**：双后端均按 Unity `Texture2D` 自底向上（`y = 0` 在底部）直接编码，相机渲染或 `Graphics.Blit` 得到的 `RenderTexture` 无需额外翻转。

---

## 开源协议与致谢

- 本项目采用 **MIT License** — 详见 [LICENSE.md](Assets/ASTC%20Encoder/LICENSE.md)。
- CPU 后端基于 [ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder)（**Apache License 2.0**）— 详见 [Third Party Notices.md](Assets/ASTC%20Encoder/Third%20Party%20Notices.md)。
