# Runtime ASTC Encoder for Unity

[English](README.md) | **简体中文**

一个面向 Unity 的**运行时 ASTC 纹理压缩库**（Unity Package: `com.aperture.astcenc`），同时提供 **CPU 原生后端**（基于 ARM 官方 [astc-encoder](https://github.com/ARM-software/astc-encoder) **`v5.7.1`** P/Invoke）与 **GPU Compute Shader 实时后端**，支持通过统一的 `Encoder` API 自由切换 `EncodeBackend`（`Cpu` / `Gpu` / `Auto`），在移动端（iOS / Android）及桌面端（Windows / macOS Editor & Standalone）运行时将 `Texture2D` 或 `RenderTexture` 实时压缩为 ASTC 格式或导出为标准 `.astc` 文件。

---

## 核心特性

- **双后端统一切换（CPU / GPU / Auto）**：
  - **CPU 后端（`EncodeBackend.Cpu`）**：基于 ARM `astc-encoder v5.7.1`，提供最高离线级压缩质量（`Fastest` ~ `Exhaustive`）、CPU 软件解压（`Decompress` / `DecompressAstcFile`）与 `parentContext` 只读查找表共享，无需 GPU Compute Shader 支持。
  - **GPU 后端（`EncodeBackend.Gpu`）**：自研跨平台无大表 HLSL Compute Shader 编码器（Metal / Vulkan / OpenGL ES 3.1+ / D3D11 / D3D12），**支持全部 6 种 BlockSize（`4x4` ~ `12x12`）及 LDR / sRGB / Normal / HDR（`CEM 11/14/15`）**，支持 **`RenderTexture` 与非可读 `Texture2D`**，对齐尺寸下可走 `CommandBuffer.CopyTexture` **纯 GPU 零 CPU 回读**路径，亦支持无卡顿 `AsyncGPUReadback` 异步管线（`CompressToTextureAsync` / `CompressTextureAsync`）。
  - **自动后端（`EncodeBackend.Auto`，全接口默认）**：设备与输入满足条件时自动优先走 GPU，不支持或运行失败时自动平滑回退到 CPU。
- **多种使用形态**：
  - 原地压缩并上传 GPU（`Texture2D.Compress`）
  - 一站式同步 / 异步生成新压缩纹理（`Texture.CompressToTexture` / `CompressToTextureAsync`，支持 `RenderTexture`、零回读拷贝、`AsyncGPUReadback` 与 **`AstcCompressOptions.GenerateMipmaps` GPU 一体化 Mipmap 生成**）
  - 扩展纹理维度与原始像素直压：`Encoder.CompressCubemap`、`Encoder.CompressTexture2DArray`、`Encoder.Encode` 的 `sourceSlice`/`destSlice` 参数，以及无需创建 `Texture2D` 的 `Encoder.CompressRawPixels` / `CompressRawPixelsAsync`（支持 `NativeArray<byte>` 与 `byte[]`）
  - **帧预算调度队列与上下文池（`AstcCompressionQueue`）**：支持 `High` / `Normal` / `Low` 优先级、`MaxGpuTexelsPerFrame` 每帧 GPU 像素预算、`MaxConcurrentCpuTasks` 并发上限以及基于 `parentContext` 表共享的 `AstcContext` 池化复用
  - **标准 KTX v1 容器与持久化磁盘缓存**：`AstcKtxContainer` / `Encoder.EncodeToKTX` / `EncodeToKTXAsync` / `LoadFromKTX`（保留完整 Mipmap 链与 UNorm/sRGB/HDR 元数据，非 ASTC 设备自动 CPU 解压回退），配合原子写入的 `AstcDiskCache`（`TryLoad` / `TryLoadAsync` / `CompressAndSaveAsync` / `LoadOrCompressAsync`）
  - **Unity Editor 可视化对比与质检窗口（`Window/ASTC Encoder/Benchmark & Inspector`）**：同屏对比 CPU 与 GPU 解码结果、放大误差热力图、PSNR / RMSE / 法线角度误差、耗时、显存节省率，并支持一键导出 `.ktx` / `.astc`
  - 底层 `CommandBuffer` 录制（`Encoder.Encode`，支持批处理、Async Compute 与动态图集/Virtual Texture 子区域更新）
  - 同步 / 异步获取非托管原始 ASTC 数据缓冲（`CompressTexture` / `CompressTextureAsync` → `AstcRaw`）
  - 统一 `AstcCompressOptions` 结构体配置（含 `Default` / `NormalMap` / `Transparent` 预设）与可复用 `AstcContext`（支持 `astcenc 5.x` `parentContext` 共享）
  - 同步 / 异步导出标准 `.astc` 字节数组（`EncodeToASTC` / `EncodeToASTCAsync`，仅编码 Mip 0）与 CPU 软件解压（`Decompress` / `DecompressToTexture` / `DecompressAstcFile`）
- **高性能低 GC 设计**：
  - **GPU 后端**：独立轻量化 `ASTC_FAST_ONLY` Kernel（极低 VGPR 寄存器占用）、**`<= 6x6` LDR 块片上 RGBA8 像素缓存**（消除多轮候选评估的重复纹理加载与 sRGB `pow`）、纯整数 ALU BISE 编码（零大常量表）、集成线性/sRGB 与法线归一化 GPU Mipmap 生成器（`AstcGpuMipGenerator.compute`）、Void-Extent 纯色块零损快速路径、Normal 专用 `CEM 4` + 切线空间角度误差 Dual-Plane、同时支持 RGB（ORM/MRAO）与 RGBA 的动态 CCS Dual-Plane、各向异性权重网格（`6x4`/`4x6`/`8x4`/`4x8`）、HDR CEM 11 高精度差值模式、量化后理想端点二次重拟合、High 档 2-Partition 搜索、零 LOH `AsyncGPUReadback` 同步回读与 `PreloadGpu` / `ReleaseGpuCache`。
  - **CPU 后端**：对 `RGBA8` / `RGBA16F` / `RGBA32F` 四通道格式走**零拷贝直传**（`NativeSlice` 指针直接传递），异步模式下**主线程仅做紧凑 `MemCpy` 快照、后台线程逐级展开 Mipmap**（峰值内存最高降低 8×），支持可选 **Unity Job System（`IJobParallelFor`）** 调度（`UseUnityJobSystem`），其余格式采用直接 `unsafe` 指针零堆分配展开，支持多核并行与 `CancellationToken` 取消。
- **丰富的输入格式、完整 Mipmap 与测试保障**：
  - 支持 24 种未压缩 `GraphicsFormat`（GPU 后端亦支持 `B10G11R11_UFloatPack32` 等 2D `RenderTexture`），涵盖 **LDR（8-bit UNorm / sRGB、16-bit UNorm）** 与 **HDR（16-bit / 32-bit Float）**。
  - 支持完整 Mipmap 链逐级压缩（兼容非正方形与非 2 的幂次纹理），内置 EditMode 交叉解码黄金单元测试（`Aperture.Astcenc.Tests`）与 CI Shader / `.meta` GUID 校验工作流。
- **贴图类型与通道自动适配**：
  - 普通贴图（`TextureType.Default`）：自动处理 `BGRA` 通道重排与 sRGB 色彩空间；支持按需开启 `useAlphaWeight` 透明通道加权（默认关闭以避免破坏 ORM / Mask 等通道打包贴图，开启时仅加权 RGB 误差并保持 Alpha 满权重）。
  - 法线贴图（`TextureType.Normal`）：按 `GGGR` 通道重排（CPU 自动启用 `MapNormal`，GPU 专用 `CEM_LDR_LUM_ALPHA_DIRECT` + 角度误差 Dual-Plane 编码，且无论源图为 sRGB 还是浮点格式均统一输出线性 UNorm ASTC）。

---

## 支持平台与原生库

预编译的 `astc-encoder v5.7.1` 原生库位于 `Assets/ASTC Encoder/Plugins/` 目录（由 Git LFS 管理）：

| 平台 | 架构 / 环境 | 原生库文件 | P/Invoke `LibraryName` | 编译特性 |
| :--- | :--- | :--- | :--- | :--- |
| **iOS** | ARM64（静态库，iOS 12.0+） | `Plugins/iOS/libastcenc-neon-static.a` | `__Internal` | ARM NEON |
| **Android** | `arm64-v8a`（API 21+） | `Plugins/Android/arm64-v8a/libastcenc-neon-shared.so` | `libastcenc-neon-shared` | ARM NEON / `c++_static` / 16KB 页对齐 |
| **Android** | `armeabi-v7a`（API 21+） | `Plugins/Android/armeabi-v7a/libastcenc-none-shared.so` | `libastcenc-none-shared` | NONE SIMD / `c++_static` / 16KB 页对齐 |
| **macOS** | Editor & Standalone Universal（11.0+） | `Plugins/Mac/libastcenc-shared.dylib` | `libastcenc-shared` | Universal（`x86_64` SSE4.1 + `x86_64h` AVX2 + `arm64` NEON） |
| **Windows** | Editor & Standalone x64 | `Plugins/x64/astcenc-sse4.1-shared.dll` | `astcenc-sse4.1-shared.dll` | SSE4.1 / 静态 MSVC 运行时（`/MT`） |

> **说明**：
> - Android 运行时会根据 `IntPtr.Size == 8` 自动在 `arm64-v8a`（NEON）与 `armeabi-v7a`（NONE）库之间分派。
> - 未提供原生 CPU 库的平台（如 Linux / WebGL）可正常通过编译；CPU 后端会准确上报不支持并安全返回错误，若设备支持 Compute Shader 仍可使用 GPU 后端。

---

## 环境要求与安装

- **Unity 版本**：`2021.3` 及以上（示例工程版本：`2021.3.6f1c1`）
- **Git LFS 与 Submodule**：
  - 预编译原生库（`.dll` / `.so` / `.a` / `.dylib`）与示例贴图由 **Git LFS** 管理；
  - `astc-encoder` 上游 C++ 源码通过 **Git Submodule** 关联在 `ThirdParty/astc-encoder`（仅在需要自行重新编译原生库时需拉取）。

```bash
git lfs install
git clone --recurse-submodules https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git
```

### 集成到现有项目

`Assets/ASTC Encoder/` 目录本身是一个标准的 Unity Package（`com.aperture.astcenc`，包内 `Aperture.Astcenc.Runtime.asmdef` 独立启用了 `allowUnsafeCode`，不影响宿主工程配置）：

- **方式一（UPM 本地 / Git 引入）**：在 Unity Package Manager 中选择 *Add package from disk...* 并指向 `Assets/ASTC Encoder/package.json`，或通过 Git URL 引入子目录：
  ```
  https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity.git?path=/Assets/ASTC%20Encoder
  ```
- **方式二（直接拷贝）**：将 `Assets/ASTC Encoder` 整个目录复制到目标项目的 `Assets/` 下即可。

---

## 快速开始

> **前置条件**：可通过 `Encoder.IsSupport(texture, backend)` 在运行时检查输入是否受支持。其中 **CPU 后端**以及**原地压缩 `Texture2D.Compress`** 要求 `Texture2D` 开启 **Read/Write Enabled**；**GPU 后端**的 `CompressToTexture` / `CompressTexture` / `EncodeToASTC` / `Encode` 支持非可读 `Texture2D` 与非 MSAA `RenderTexture`。

### 1. 原地压缩纹理

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;

public void CompressInPlace(Texture2D texture)
{
    if (!Encoder.IsSupport(texture))
    {
        Debug.LogError("Texture is not readable or format is unsupported.");
        return;
    }

    // 默认 EncodeBackend.Auto（优先 GPU，否则回退 CPU）
    // threadCount 仅用于 CPU 后端：1 为单线程；<= 0 自动按 CPU 核心数选择（最多 4 线程）
    bool ok = texture.Compress(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        makeNoLongerReadable: true,
        threadCount: 1
    );
}
```

### 2. 后台异步 + 多线程压缩（推荐移动端使用）

在 GPU 支持时走 `AsyncGPUReadback`，否则在主线程抓取像素快照并交由后台多线程压缩，避免主线程掉帧：

```csharp
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using UnityEngine;

public async Task<Texture2D> CompressAsync(Texture2D inputTexture)
{
    using AstcRaw raw = await inputTexture.CompressTextureAsync(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        threadCount: 0 // CPU 后端 <= 0 自动选择多线程
    );

    if (raw == null)
        return null;

    Texture2D outputTexture = new Texture2D(
        inputTexture.width,
        inputTexture.height,
        TextureFormat.ASTC_6x6,
        inputTexture.mipmapCount > 1,
        false);

    outputTexture.name = inputTexture.name;
    outputTexture.LoadRawTextureData(raw);
    outputTexture.Apply(false, true);
    return outputTexture;
}
```

### 3. 复用 `AstcContext` 批量压缩同规格纹理（CPU 后端）

批量压缩格式相同的纹理时，复用 `AstcContext` 可避免重复执行昂贵的 `astcenc_context_alloc`：

```csharp
using System.Collections.Generic;
using Aperture.Astcenc.Runtime;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public void CompressBatch(IList<Texture2D> textures)
{
    using AstcContext context = AstcContext.Create(
        GraphicsFormat.R8G8B8A8_SRGB,
        TextureType.Default,
        BlockSize.ASTC_6x6,
        CompressQuality.Fast,
        threadCount: 0);

    if (context == null)
        return;

    foreach (Texture2D tex in textures)
    {
        using AstcRaw raw = context.CompressTexture(tex, TextureType.Default);
        if (raw != null && tex.Reinitialize(tex.width, tex.height, GraphicsFormat.RGBA_ASTC6X6_SRGB, tex.mipmapCount > 1))
        {
            tex.LoadRawTextureData(raw);
            tex.Apply(false, true);
        }
    }
}
```

### 4. 编码并保存为标准 `.astc` 文件

```csharp
using System.IO;
using Aperture.Astcenc.Runtime;
using UnityEngine;

public void SaveAsAstcFile(Texture2D inputTexture, string outputPath)
{
    // 严格仅编码 Mip 0 并拼接 16 字节标准 ASTC 文件头
    byte[] fileData = inputTexture.EncodeToASTC(
        TextureType.Default,
        BlockSize.ASTC_6x6,
        CompressQuality.Medium);

    if (fileData != null)
    {
        File.WriteAllBytes(outputPath, fileData);
    }
}
```

### 5. GPU 实时压缩（零回读 / 支持 `RenderTexture` 与非可读 `Texture2D`）

```csharp
using Aperture.Astcenc.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

public Texture2D CompressOnGpu(Texture source) // 支持 Texture2D（无需 Read/Write）与 RenderTexture
{
    // 1. 一站式生成压缩 Texture2D（对齐尺寸自动走零回读 Graphics.CopyTexture，否则走 GPU Buffer 安全回退）
    return source.CompressToTexture(
        textureType: TextureType.Default,
        blockSize: BlockSize.ASTC_6x6,
        quality: CompressQuality.Fast,
        includeMipmaps: true,
        backend: EncodeBackend.Gpu);
}

public void EncodeIntoAtlas(CommandBuffer cmd, Texture tileSrc, Texture2D astcAtlas, int tileX, int tileY)
{
    // 2. 底层 CommandBuffer 录制（支持批处理 / Async Compute / Virtual Texture 子区域编码）
    Encoder.Encode(cmd, tileSrc, astcAtlas, TextureType.Default, BlockSize.ASTC_4x4, CompressQuality.Fast,
        sourceMip: 0, destMip: 0, destX: tileX, destY: tileY);
}
```

仓库附带示例场景 `Assets/Scenes/SampleScene.unity` 与脚本 `Assets/Scripts/Sample.cs`，可在 Inspector 中直接切换 `EncodeBackend`（`Cpu` / `Gpu` / `Auto`）运行体验。

---

## API 参考

所有对外 API 位于命名空间 `Aperture.Astcenc.Runtime` 下。

### `Encoder`（统一压缩入口，支持 `EncodeBackend` 切换）

| 方法 / 属性 | 说明 |
| :--- | :--- |
| `Encoder.IsGpuSupported` | 检测当前设备是否支持 GPU Compute Shader 与 `R32G32B32A32_UInt` 写入 |
| `Encoder.SupportsGpuDirectCopy(blockSize, srgb, isHdr)` | 检测当前 GPU 是否支持零回读 `CopyTexture` 直出目标 ASTC 格式纹理 |
| `Encoder.GetTargetAstcFormat(sourceFormat, blockSize, textureType)` | 推导源格式对应的目标 ASTC `GraphicsFormat`（法线贴图统一映射为线性 `_UNorm`） |
| `Encoder.PreloadGpu(blockSize)` / `Encoder.ReleaseGpuCache()` | 预热 GPU Fast 与 Full 全套 Compute Shader Kernel（含 Texture 与 Buffer 输出）/ 释放缓存的 Shader 引用 |
| `Encoder.IsSupport(Texture texture, EncodeBackend backend = EncodeBackend.Auto)` | 检查纹理是否满足指定后端要求（默认 `Auto`；GPU 后端无需 `isReadable` 且支持非 MSAA `RenderTexture`） |
| `Texture.CompressToTexture(..., backend = EncodeBackend.Auto)` / `Texture.CompressToTextureAsync(...)` | 压缩任意 2D `Texture` / `RenderTexture` 并返回新的压缩 `Texture2D`（对齐时走零回读 CopyTexture；异步版自动走 `AsyncGPUReadback` 或 CPU 后台线程，不阻塞主线程） |
| `Encoder.Encode(CommandBuffer cmd, source, destination, ...)` | 向 `CommandBuffer` 录制 GPU ASTC 编码与 `CopyTexture`（`destX`/`destY` 为块对齐的目标像素坐标，并严格校验目标色彩空间与格式匹配） |
| `Texture2D.Compress(..., backend = EncodeBackend.Auto)` | 原地压缩 `Texture2D`（默认 `Auto`；因调用 `Reinitialize`，需开启 `isReadable` 且不支持部分 Mip 链；对齐尺寸且 `makeNoLongerReadable == true` 时走零回读路径；非可读纹理请改用 `CompressToTexture`） |
| `Texture2D.CompressTexture(..., backend = EncodeBackend.Auto)` / `RenderTexture.CompressTexture(...)` | 同步压缩全部 Mipmap 至非托管内存，返回 `AstcRaw`（需 `Dispose()`） |
| `Texture2D.CompressTextureAsync(..., backend = EncodeBackend.Auto)` / `RenderTexture.CompressTextureAsync(...)` | 异步压缩（默认 `Auto`；GPU `AsyncGPUReadback` / CPU 后台多线程），均支持 `CancellationToken` |
| `Texture2D.LoadRawTextureData(AstcRaw raw)` | 将 `AstcRaw` 中的压缩数据直接加载到目标 `Texture2D` |
| `Texture.EncodeToASTC(..., backend = EncodeBackend.Auto)` / `Texture.EncodeToASTCAsync(..., backend = EncodeBackend.Auto)` | 仅压缩 Mip 0（同时支持 `Texture2D` 与 `RenderTexture`）并写入 16 字节标准 `.astc` 文件头，返回托管 `byte[]` |
| `Encoder.Decompress(...)` / `Encoder.DecompressToTexture(...)` / `Encoder.DecompressAstcFile(...)` | 基于原生 CPU `astc-encoder` 软解原始 ASTC 块数据或 `.astc` 文件为 RGBA 字节数组或未压缩 `Texture2D` |

> 所有压缩入口均提供 `AstcCompressOptions` 结构体配置重载（内置 `Default`、`NormalMap`、`Transparent` 预设），可按需启用 CPU 后端的 `UsePerceptual`、`AlphaScaleRadius` 等高级参数。

### `AstcContext`（可复用原生 CPU 编码上下文）

| 成员 | 说明 |
| :--- | :--- |
| `AstcContext.Create(graphicsFormat, textureType, blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, parentContext)` | 创建可复用压缩上下文（`threadCount <= 0` 自动选择线程数；可选传入 `parentContext` 共享 `astcenc 5.x` 只读查找表） |
| `AstcContext.CreateDecompressor(blockSize, srgb, isHdr, threadCount)` | 创建轻量级 `DecompressOnly` 解压上下文 |
| `context.CompressTexture(texture, textureType)` | 复用当前上下文同步压缩纹理 |
| `context.CompressTextureAsync(texture, textureType, cancellationToken)` | 复用当前上下文异步压缩纹理（主线程仅做紧凑 memcpy，像素展开下沉至后台线程逐级执行） |
| `context.Dispose()` | 线程安全释放底层 `astcenc_context` 原生内存 |

### `AstcRaw`（非托管压缩数据封装）

- `Length`：压缩后总字节数
- `IsDisposed`：是否已释放
- `CopyTo(byte[] destination, int destinationOffset = 0)` / `ToArray()`：安全拷贝原始压缩字节到托管数组
- `Dispose()`：线程安全释放底层非托管内存

### 枚举与配置参数

- **`AstcCompressOptions`**：统一编码配置结构体（内置 `Default`、`NormalMap`、`Transparent` 预设），收口基础与高级编码参数
- **`EncodeBackend`**：`Cpu`（原生 `astc-encoder`）、`Gpu`（Compute Shader 实时编码）、`Auto`（优先 GPU，不支持或运行失败时自动回退 CPU）
- **`TextureType`**：`Default`（普通颜色贴图）、`Normal`（法线贴图——无论输入为 sRGB 还是浮点格式均统一按线性 UNorm 编码）
- **`BlockSize`**（CPU 与 GPU 均完整支持以下 6 种，每块固定 16 字节）：
  - `ASTC_4x4` (8.00 bpp) / `ASTC_5x5` (5.12 bpp) / `ASTC_6x6` (3.56 bpp) / `ASTC_8x8` (2.00 bpp) / `ASTC_10x10` (1.28 bpp) / `ASTC_12x12` (0.89 bpp)
- **`CompressQuality`**：
  - CPU 后端映射到 `astcenc` 质量参数：`Fastest` (`0`) / `Fast` (`10`) / `Medium` (`60`) / `Thorough` (`98`) / `VeryThorough` (`99`) / `Exhaustive` (`100`)
  - GPU 后端映射到 3 档策略：`Fastest`/`Fast` → 独立轻量化 `ASTC_FAST_ONLY` Kernel（大幅降低 VGPR 寄存器压力）；`Medium` → 最小二乘端点微调 + 正方形与各向异性（`6x4`/`4x6`/`8x4`/`4x8`）多 BlockMode 择优 + 动态 CCS Dual-Plane（同时覆盖 RGB 与 RGBA）+ 法线切线空间角度误差 + 量化后理想端点二次重拟合 + HDR CEM 11 高精度差值模式；`Thorough`~`Exhaustive` → 叠加 16 方向精选 2-Partition 搜索
- **`CodecFlags`**：映射 `astcenc 5.7.1` 标志位（`MapNormal`、`UseDecodeUnorm8`、`UseAlphaWeight`、`UsePerceptual`、`DecompressOnly`、`SelfDecompressOnly`、`MapRGBM`）

### 支持的输入格式与目标格式映射

| 输入 `GraphicsFormat` 类别 | 支持的具体格式 | `astcenc` 色彩配置 | 内部传输路径 | 目标 ASTC `GraphicsFormat` |
| :--- | :--- | :--- | :--- | :--- |
| **LDR 8-bit sRGB** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_SRGB`) | `ASTCENC_PRF_LDR_SRGB`（Normal 为 `LDR`） | `U8`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_SRGB`（Normal 为 `_UNorm`） |
| **LDR 8-bit Linear** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_UNorm`) | `ASTCENC_PRF_LDR` | `U8`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UNorm` |
| **LDR 16-bit Linear** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_UNorm`) | `ASTCENC_PRF_LDR` | `F32`（无损保留 16-bit 精度） | `RGBA_ASTC{N}X{N}_UNorm` |
| **HDR 16-bit Float** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_SFloat`) | `ASTCENC_PRF_HDR` | `F16`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UFloat` |
| **HDR 32-bit Float** | `R32` / `R32G32` / `R32G32B32` / `R32G32B32A32` (`_SFloat`) | `ASTCENC_PRF_HDR` | `F32`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UFloat` |

---

## 实现原理与架构

```mermaid
flowchart TD
    A["Unity Texture2D / RenderTexture"] --> B["C# 统一入口 (Encoder.cs)"]
    B -->|GPU 后端| G1["AstcGpuBackend.cs"]
    G1 --> G2["AstcGpuEncoder.compute (HLSL 纯 ALU BISE / CEM 4,8,11,12,14,15)"]
    G2 -->|对齐且支持 RTToTexture| G3["CommandBuffer.CopyTexture 零回读写入 ASTC Texture2D"]
    G2 -->|Buffer / AsyncGPUReadback| G4["AstcRaw (非托管压缩数据)"]
    B -->|CPU 后端| C1["AstcCpuBackend.cs / AstcContext.cs"]
    C1 --> C2["NativeAPI P/Invoke (astc-encoder v5.7.1)"]
    C2 --> G4
    G4 --> H1["Texture2D.Reinitialize / LoadRawTextureData + Apply"]
    G4 --> H2["AstcHeader.WriteTo (16B) + Mip 0 → 标准 .astc 文件"]
```

### 核心流程说明

1. **上下文管理（`AstcContext`）**：通过 `astcenc_config_init` + `astcenc_context_alloc` 创建支持 `SelfDecompressOnly`（或通过 `CreateDecompressor` 创建 `DecompressOnly`）的原生上下文，支持可选 `parentContext` 共享 `astcenc 5.x` 只读查找表；支持多核并行压缩（`Parallel.For` 分配唯一 `thread_index`，小尺寸 Mipmap 自动退回单线程）与 `CancellationToken` 取消（`astcenc_compress_cancel`），并在 `finally` 中保证 `astcenc_compress_reset`。
2. **输出缓冲预分配**：按每级 `ceil(max(1, w >> i) / blockX) * ceil(max(1, h >> i) / blockY) * 16` 字节累加所有 Mipmap 总长度，一次性分配连续非托管内存。
3. **像素传输优化**：
   - `RGBA8` / `RGBA16F` / `RGBA32F`（含 `BGRA8`，通道重排交由原生 `swizzle` 处理）内存布局已与 `astcenc_image` 对齐：同步模式直接传递 `NativeSlice` 只读指针（零拷贝）。
   - 异步模式在主线程仅执行一次紧凑 `UnsafeUtility.MemCpy` 快照，将像素格式展开下沉至后台线程逐级 Mipmap 按需执行并即刻释放（1/2/3 通道与 16-bit 格式峰值内存最高降低 8×）。
   - 1/2/3 通道与 16-bit UNorm 格式通过内部 `IRawData` 直接 `unsafe` 指针写入 4 通道缓冲，零堆分配（缺失 RGB 补 `0`，缺失 Alpha 补 `1.0` / `0xFF`；16-bit UNorm 直接输出 `F32` 保留全部 16-bit 精度）。
4. **`.astc` 导出与解码**：`EncodeToASTC` 仅压缩 Mip 0，并通过静态方法 `AstcHeader.WriteTo` 直接写入 16 字节小端文件头（魔数 `0x5CA1AB13` + 3 字节块尺寸 + 3 × 3 字节 24-bit 图像尺寸）；同时提供 `AstcHeader.TryParse` 与 `Encoder.DecompressAstcFile` 对称软解。

---

## 跨平台原生库自动构建（GitHub Actions）

仓库内置跨平台自动构建工作流 [.github/workflows/build-native-plugins.yml](.github/workflows/build-native-plugins.yml)，可直接基于 `ThirdParty/astc-encoder` 子模块一次编出全部 5 个平台产物（Android `.so` 自动 Strip 调试符号并启用 16KB 页对齐）并校验导出符号：

- **自动触发**：当更新 `ThirdParty/astc-encoder` 子模块或修改工作流文件并推送时，自动编译并打包为 `astcenc-unity-plugins-<version>` Artifact。
- **手动触发并自动提交**：在 GitHub Actions 页面运行 `Build Native ASTC Encoder Plugins` 并勾选 `commit_binaries`，工作流会自动将编出的 5 个原生库通过 Git LFS 提交回当前分支的 `Assets/ASTC Encoder/Plugins/` 目录。

> 升级上游 `astc-encoder` 子模块版本时，请对照 `ThirdParty/astc-encoder/Source/astcenc.h` 核对 `Aperture.Astcenc.Runtime.NativeAPI` 中的结构体字段与函数签名是否发生 ABI 变更。

---

## 目录结构

```text
.
├── .github/workflows/
│   └── build-native-plugins.yml           # 全平台原生库自动编译与打包工作流
├── .gitmodules                            # astc-encoder 子模块配置
├── ThirdParty/
│   └── astc-encoder/                      # ARM-software/astc-encoder v5.7.1 源码子模块
├── Assets/
│   ├── ASTC Encoder/                      # Unity Package (com.aperture.astcenc v2.0.0)
│   │   ├── package.json
│   │   ├── LICENSE.md                     # MIT License
│   │   ├── Third Party Notices.md         # ARM astc-encoder Apache-2.0 License
│   │   ├── Plugins/                       # 各平台预编译 v5.7.1 原生库 (Git LFS)
│   │   │   ├── Android/arm64-v8a/libastcenc-neon-shared.so
│   │   │   ├── Android/armeabi-v7a/libastcenc-none-shared.so
│   │   │   ├── iOS/libastcenc-neon-static.a
│   │   │   ├── Mac/libastcenc-shared.dylib
│   │   │   └── x64/astcenc-sse4.1-shared.dll
│   │   └── Runtime/
│   │       ├── Aperture.Astcenc.Runtime.asmdef
│   │       ├── AstcCompressOptions.cs     # 统一编码配置结构体 (Default / NormalMap / Transparent)
│   │       ├── AstcContext.cs             # 可复用 CPU 编码上下文（多线程 / 异步 / parentContext / 解压）
│   │       ├── Encoder.cs                 # 统一压缩与软解入口（支持 Cpu / Gpu / Auto 后端切换）
│   │       ├── EncodeBackend.cs           # 编码后端枚举 (Cpu / Gpu / Auto)
│   │       ├── AstcRaw.cs                 # 非托管 ASTC 压缩数据封装 (IDisposable)
│   │       ├── AstcHeader.cs              # 16 字节标准 .astc 文件头写入与解析工具
│   │       ├── ImageExtensions.cs         # astcenc_image 零拷贝 / MemCpy / 像素转换 (internal)
│   │       ├── RawTextureData.cs          # 16 种像素格式直接 unsafe 指针写入器 (internal)
│   │       ├── BlockSize.cs               # ASTC 块尺寸枚举 (4x4 ~ 12x12)
│   │       ├── CompressQuality.cs         # 压缩质量预设枚举
│   │       ├── TextureType.cs             # 贴图类型枚举 (Default / Normal)
│   │       ├── CodecFlags.cs              # astcenc 编码标志位枚举
│   │       ├── Cpu/                       # CPU 原生后端实现 (astc-encoder P/Invoke)
│   │       │   └── AstcCpuBackend.cs      # CPU 同步/异步压缩、紧凑快照与 CPU 软解
│   │       ├── Gpu/                       # GPU Compute Shader 后端实现
│   │       │   ├── AstcGpuBackend.cs      # GPU 调度、Fast/Full Kernel、CopyTexture 与 AsyncGPUReadback
│   │       │   └── Shaders/
│   │       │       ├── AstcGpuBise.hlsl   # 无大表纯 ALU BISE、量化/反量化与 128-bit 组装
│   │       │       ├── AstcGpuColor.hlsl  # sRGB、HDR LNS/CEM11 差值、2-Partition 哈希与双线性插值
│   │       │       └── AstcGpuCore.hlsl   # 4x4~12x12、LDR/Normal/HDR、动态 CCS Dual-Plane 与端点重拟合
│   │       ├── Resources/
│   │       │   └── AstcGpuEncoder.compute # Unity ComputeShader（6 BlockSize × 2 输出 × Fast/Full）
│   │       └── NativeAPI/                 # Aperture.Astcenc.Runtime.NativeAPI (v5.7.1 P/Invoke)
│   │           ├── astcenc_api.cs
│   │           ├── astcenc_config.cs
│   │           ├── astcenc_error.cs
│   │           ├── astcenc_image.cs
│   │           ├── astcenc_profile.cs
│   │           ├── astcenc_swizzle.cs
│   │           ├── astcenc_swz.cs
│   │           └── astcenc_type.cs
│   ├── Resources/                         # 示例贴图 (Color.png, Normal.png)
│   ├── Scenes/SampleScene.unity           # 示例场景
│   └── Scripts/Sample.cs                  # 示例脚本（支持 Inspector 切换 Cpu / Gpu / Auto）
├── Packages/
├── ProjectSettings/
├── README.md                              # English README
└── README_CN.md                           # 简体中文 README
```

---

## 注意事项

- **非托管内存管理**：直接调用 `CompressTexture` / `CompressTextureAsync` 返回的 `AstcRaw` 持有非托管内存，以及手动创建的 `AstcContext` 均需通过 `using` 或 `Dispose()` 及时释放。
- **GPU 后端平台特性**：
  - 在支持 ASTC 硬件采样的 GPU（iOS/macOS Apple Silicon Metal、Android Vulkan/GLES 3.1+）上，`CompressToTexture` / `Compress` / `Encoder.Encode` 可直接生成压缩 `Texture2D`（尺寸与 BlockSize 对齐时走零回读 `CopyTexture`，非对齐尺寸或完整 Mip 尾级自动走 GPU Buffer 安全上传）。
  - 在不支持 ASTC 硬件采样的桌面 GPU（如 Windows D3D11/D3D12）上，GPU 后端仍可通过 `CompressTexture` / `CompressTextureAsync` / `EncodeToASTC` / `EncodeToASTCAsync` 高速生成原始 ASTC 数据或 `.astc` 文件；在 Unity Editor 中，`Compress` / `CompressToTexture` 也会自动走 GPU Buffer + `LoadRawTextureData` 回退路径并由 Editor 软件预览（非 Editor 独立包若 GPU 不支持 ASTC 采样则会提前报错拦截）。
  - 建议在启动或场景切换时调用 `Encoder.PreloadGpu(...)` 预热 Compute Shader，切换场景或不再使用时调用 `Encoder.ReleaseGpuCache()` 释放缓存的 Compute Shader 引用。`Encoder.Encode(CommandBuffer, ...)` 使用 `CommandBuffer.GetTemporaryRT` 申请中间块纹理，命令缓冲可安全地延迟执行或多次复用。
- **移动端性能建议**：高频/动态贴图推荐优先使用 `EncodeBackend.Gpu` 配合 `CompressQuality.Fast`；CPU 后端压缩大图建议使用 `CompressTextureAsync` 配合 `Fastest` / `Fast` 并复用 `AstcContext`。
- **异步压缩上下文生命周期**：使用 `context.CompressTextureAsync(...)` 时，请确保在返回的 `Task<AstcRaw>` 完成前不要释放该 `AstcContext`。
- **透明贴图加权**：对于带透明通道的 UI / Sprite / 植被贴图，建议传入 `useAlphaWeight: true`；对于 ORM / Mask 等通道打包贴图保持默认 `useAlphaWeight: false`。
- **贴图纵向坐标约定**：GPU 与 CPU 后端均直接按 Unity `Texture2D` / `.astc` 规范编码（第 0 行为贴图底部，`y = 0` 不做额外翻转）。Unity 相机渲染或 `Graphics.Blit` 输出的 `RenderTexture` 与 `Texture2D` 布局一致，可直接压缩；若 `RenderTexture` 是由自定义 Compute Shader 按自顶向下坐标写入的，需在压缩前由调用方自行翻转 Y 轴。

---

## 许可证

- 本项目代码采用 **MIT License**，详见 [Assets/ASTC Encoder/LICENSE.md](Assets/ASTC%20Encoder/LICENSE.md)。
- 底层编码库 [ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder) 采用 **Apache License 2.0**，详见 [Assets/ASTC Encoder/Third Party Notices.md](Assets/ASTC%20Encoder/Third%20Party%20Notices.md)。
