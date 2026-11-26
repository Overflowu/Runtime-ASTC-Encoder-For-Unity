# Runtime ASTC Encoder for Unity

一个面向 Unity 的**运行时 ASTC 纹理压缩库**（Unity Package: `com.aperture.astcenc`），基于 ARM 官方开源的 [astc-encoder](https://github.com/ARM-software/astc-encoder) **`v5.7.1`** 交叉编译原生库，通过 C# **P/Invoke** 调用原生编码器，支持在移动端（iOS / Android）及桌面端（Windows / macOS Editor & Standalone）运行时将未压缩的 `Texture2D` 实时压缩为 ASTC 格式或导出为标准 `.astc` 文件。

---

## 核心特性

- **运行时动态压缩**：无需依赖离线导入管线，可在运行时对动态生成或网络下载的 `Texture2D` 执行 ASTC 压缩，显著降低移动端显存占用。
- **多种使用形态**：
  - 原地压缩并上传 GPU（`Texture2D.Compress`）
  - 同步 / 异步获取非托管原始 ASTC 数据缓冲（`Texture2D.CompressTexture` / `Texture2D.CompressTextureAsync` → `AstcRaw`）
  - 复用编码上下文批量压缩（`AstcContext`）
  - 导出符合标准容器规范的 `.astc` 字节数组（`Texture2D.EncodeToASTC`，仅编码 Mip 0）
- **高性能低 GC 设计**：
  - 对 `RGBA8` / `RGBA16F` / `RGBA32F` 四通道格式走**零拷贝直传**（`NativeSlice` 指针直接传递，异步模式下采用原生 `MemCpy` 快照）。
  - 其余像素格式采用无堆分配的 `Marshal.WriteInt16` / `Marshal.WriteInt32` 与指针遍历，消除逐像素 GC 垃圾。
  - 支持**多核并行压缩**（`threadCount`）、**后台异步压缩**（不阻塞主线程）以及 **`AstcContext` 上下文复用**（默认开启 `SelfDecompressOnly` 缩短初始化耗时）。
- **丰富的输入格式与完整 Mipmap 支持**：
  - 支持 24 种未压缩 `GraphicsFormat`，涵盖 **LDR（8-bit UNorm / sRGB、16-bit UNorm）** 与 **HDR（16-bit / 32-bit Float）**。
  - 支持完整 Mipmap 链逐级压缩（兼容非正方形纹理）。
- **贴图类型与通道自动适配**：
  - 普通贴图（`TextureType.Default`）：自动处理 `BGRA` 通道重排，对四通道贴图支持 `UseAlphaWeight` 误差加权。
  - 法线贴图（`TextureType.Normal`）：自动启用 `MapNormal` 角度误差优化（不叠加 `UseAlphaWeight`），并按 `GGGR` 通道重排（同时兼容 RGBA 与 BGRA 输入）。

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
> - 未提供原生库的平台（如 Linux / WebGL）可正常通过编译，调用时安全返回 `ASTCENC_ERR_NOT_IMPLEMENTED`。

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

> **前置条件**：待压缩的输入 `Texture2D` 必须开启 **Read/Write Enabled**，且格式为受支持的**未压缩** `GraphicsFormat`（可通过 `Encoder.IsSupport(texture)` 在运行时检查）。

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

    // threadCount = 1 为单线程；<= 0 自动按 CPU 核心数选择（最多 4 线程）
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

在主线程自动抓取像素快照并在后台多线程执行压缩，完成后回到主线程上传 GPU，避免掉帧：

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
        threadCount: 0 // <= 0 自动选择多线程
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

### 3. 复用 `AstcContext` 批量压缩同规格纹理

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

仓库附带示例场景 `Assets/Scenes/SampleScene.unity` 与脚本 `Assets/Scripts/Sample.cs`，可直接打开运行。

---

## API 参考

所有对外 API 位于命名空间 `Aperture.Astcenc.Runtime` 下。

### `Encoder`（`Texture2D` 扩展方法）

| 方法 | 说明 |
| :--- | :--- |
| `Encoder.IsSupport(Texture texture)` | 检查纹理非空、满足 `isReadable` 且 `graphicsFormat` 受支持 |
| `Texture2D.Compress(textureType, blockSize, quality, makeNoLongerReadable, threadCount)` | 压缩纹理并原地 `Reinitialize`、`LoadRawTextureData`、`Apply(false, ...)` |
| `Texture2D.CompressTexture(textureType, blockSize, quality, threadCount)` | 同步压缩全部 Mipmap 至非托管内存，返回 `AstcRaw`（需 `Dispose()`） |
| `Texture2D.CompressTextureAsync(textureType, blockSize, quality, threadCount)` | 主线程快照像素 + 后台多线程异步压缩，返回 `Task<AstcRaw>` |
| `Texture2D.LoadRawTextureData(AstcRaw raw)` | 将 `AstcRaw` 中的压缩数据直接加载到目标 `Texture2D` |
| `Texture2D.EncodeToASTC(textureType, blockSize, quality, threadCount)` | 仅压缩 Mip 0 并写入 16 字节标准 ASTC 文件头，返回托管 `byte[]` |

### `AstcContext`（可复用原生编码上下文）

| 成员 | 说明 |
| :--- | :--- |
| `AstcContext.Create(graphicsFormat, textureType, blockSize, quality, threadCount)` | 创建可复用上下文（`threadCount <= 0` 自动选择线程数） |
| `context.CompressTexture(texture, textureType)` | 复用当前上下文同步压缩纹理 |
| `context.CompressTextureAsync(texture, textureType)` | 复用当前上下文异步压缩纹理（任务完成前需保持上下文未释放） |
| `context.Dispose()` | 释放底层 `astcenc_context` 原生内存 |

### 枚举与配置参数

- **`TextureType`**：`Default`（普通颜色贴图）、`Normal`（法线贴图）
- **`BlockSize`**（每块固定 16 字节，块越大压缩率越高）：
  - `ASTC_4x4` (8.00 bpp) / `ASTC_5x5` (5.12 bpp) / `ASTC_6x6` (3.56 bpp) / `ASTC_8x8` (2.00 bpp) / `ASTC_10x10` (1.28 bpp) / `ASTC_12x12` (0.89 bpp)
- **`CompressQuality`**（映射到 `astcenc` 质量参数 `0 ~ 100`）：
  - `Fastest` (`0`) / `Fast` (`10`) / `Medium` (`60`) / `Thorough` (`98`) / `VeryThorough` (`99`) / `Exhaustive` (`100`)
- **`CodecFlags`**：映射 `astcenc 5.7.1` 标志位（`MapNormal`、`UseDecodeUnorm8`、`UseAlphaWeight`、`UsePerceptual`、`DecompressOnly`、`SelfDecompressOnly`、`MapRGBM`）

### 支持的输入格式与目标格式映射

| 输入 `GraphicsFormat` 类别 | 支持的具体格式 | `astcenc` 色彩配置 | 内部传输路径 | 目标 ASTC `GraphicsFormat` |
| :--- | :--- | :--- | :--- | :--- |
| **LDR 8-bit sRGB** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_SRGB`) | `ASTCENC_PRF_LDR_SRGB` | `U8`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_SRGB` |
| **LDR 8-bit Linear** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_UNorm`) | `ASTCENC_PRF_LDR` | `U8`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UNorm` |
| **LDR 16-bit Linear** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_UNorm`) | `ASTCENC_PRF_LDR` | `F16`（归一化转换） | `RGBA_ASTC{N}X{N}_UNorm` |
| **HDR 16-bit Float** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_SFloat`) | `ASTCENC_PRF_HDR` | `F16`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UFloat` |
| **HDR 32-bit Float** | `R32` / `R32G32` / `R32G32B32` / `R32G32B32A32` (`_SFloat`) | `ASTCENC_PRF_HDR` | `F32`（4 通道零拷贝直传） | `RGBA_ASTC{N}X{N}_UFloat` |

---

## 实现原理与架构

```mermaid
flowchart TD
    A["Unity Texture2D (未压缩 / 可读)"] --> B["C# 封装层 (Encoder.cs / AstcContext.cs)"]
    B --> C["参数推导: BlockSize / Quality / Profile / Swizzle / Flags"]
    B --> D1["同步 4 通道: 零拷贝指针 (LoadFromDirect)"]
    B --> D2["异步 / 非 4 通道: MemCpy 或无 GC 像素补齐 (ImageExtensions.cs + RawTexutureData.cs)"]
    C --> E["P/Invoke 绑定层 (NativeAPI/astcenc_api.cs, v5.7.1 ABI)"]
    D1 --> E
    D2 --> E
    E --> F["原生库 astc-encoder 5.7.1 (.a / .so / .dylib / .dll)"]
    F --> G["AstcRaw (非托管压缩数据)"]
    G --> H1["Texture2D.Reinitialize + LoadRawTextureData + Apply(false, ...)"]
    G --> H2["AstcHeader.WriteTo (16B) + Mip 0 → 标准 .astc 文件"]
```

### 核心流程说明

1. **上下文管理（`AstcContext`）**：通过 `astcenc_config_init` + `astcenc_context_alloc(..., parent_context: IntPtr.Zero)` 创建支持 `SelfDecompressOnly` 的原生上下文；支持多核并行压缩（`Parallel.For` 分配唯一 `thread_index`，小尺寸 Mipmap 自动退回单线程以避免调度开销），每次压缩后在 `finally` 中调用 `astcenc_compress_reset`。
2. **输出缓冲预分配**：按每级 `ceil(max(1, w >> i) / blockX) * ceil(max(1, h >> i) / blockY) * 16` 字节累加所有 Mipmap 总长度，一次性分配连续非托管内存。
3. **像素传输优化**：
   - `RGBA8` / `RGBA16F` / `RGBA32F`（含 `BGRA8`，通道重排交由原生 `swizzle` 处理）内存布局已与 `astcenc_image` 对齐：同步模式直接传递 `NativeSlice` 只读指针（零拷贝），异步模式用 `UnsafeUtility.MemCpy` 整块快照。
   - 1/2/3 通道与 16-bit UNorm 格式通过 `IRawData` 指针循环与 `Marshal.WriteInt16` / `WriteInt32` 写入 4 通道缓冲，零堆分配（缺失 RGB 补 `0`，缺失 Alpha 补 `1.0` / `0xFF`）。
4. **`.astc` 导出**：`EncodeToASTC` 仅压缩 Mip 0，并通过静态方法 `AstcHeader.WriteTo` 直接写入 16 字节小端文件头（魔数 `0x5CA1AB13` + 3 字节块尺寸 + 3 × 3 字节 24-bit 图像尺寸），不产生额外的非托管头分配与 Finalizer 开销。

---

## 跨平台原生库自动构建（GitHub Actions）

仓库内置跨平台自动构建工作流 [.github/workflows/build-native-plugins.yml](.github/workflows/build-native-plugins.yml)，可直接基于 `ThirdParty/astc-encoder` 子模块一次编出全部 5 个平台产物并校验导出符号：

- **自动触发**：当更新 `ThirdParty/astc-encoder` 子模块或修改工作流文件并推送时，自动编译并打包为 `astcenc-unity-plugins-<version>` Artifact。
- **手动触发并自动提交**：在 GitHub Actions 页面运行 `Build Native ASTC Encoder Plugins` 并勾选 `commit_binaries`，工作流会自动将编出的 5 个原生库通过 Git LFS 提交回当前分支的 `Assets/ASTC Encoder/Plugins/` 目录。

> 升级上游 `astc-encoder` 子模块版本时，请对照 `ThirdParty/astc-encoder/Source/astcenc.h` 核对 `NativeAPI/` 中的结构体字段与函数签名是否发生 ABI 变更。

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
│   ├── ASTC Encoder/                      # Unity Package (com.aperture.astcenc)
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
│   │       ├── AstcContext.cs             # 可复用编码上下文（多线程 / 异步支持）
│   │       ├── Encoder.cs                 # 核心压缩流程与 Texture2D 扩展方法
│   │       ├── AstcRaw.cs                 # 非托管 ASTC 压缩数据封装 (IDisposable)
│   │       ├── AstcHeader.cs              # 16 字节标准 .astc 文件头工具
│   │       ├── ImageExtensions.cs         # astcenc_image 零拷贝 / MemCpy / 像素转换
│   │       ├── RawTexutureData.cs         # 16 种像素格式无 GC 写入结构体
│   │       ├── BlockSize.cs               # ASTC 块尺寸枚举
│   │       ├── CompressQuality.cs         # 压缩质量预设枚举
│   │       ├── TextureType.cs             # 贴图类型枚举 (Default / Normal)
│   │       ├── CodecFlags.cs              # astcenc 编码标志位枚举
│   │       └── NativeAPI/                 # astcenc 5.7.1 C API P/Invoke 绑定
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
│   └── Scripts/Sample.cs                  # 示例脚本
├── Packages/
├── ProjectSettings/
└── README.md
```

---

## 注意事项

- **非托管内存管理**：直接调用 `CompressTexture` / `CompressTextureAsync` 返回的 `AstcRaw` 持有非托管内存，以及手动创建的 `AstcContext` 均需通过 `using` 或 `Dispose()` 及时释放。
- **移动端性能建议**：运行时压缩大图建议使用 `CompressTextureAsync` 配合 `Fastest` / `Fast` 质量档位，避免阻塞主线程；批量同格式压缩请复用 `AstcContext`。
- **异步压缩上下文生命周期**：使用 `context.CompressTextureAsync(...)` 时，请确保在返回的 `Task<AstcRaw>` 完成前不要释放该 `AstcContext`。
- **通道打包贴图说明**：当前对 `TextureType.Default` 的四通道格式默认启用 `UseAlphaWeight`（按 Alpha 透明度加权 RGB 误差）；若压缩 ORM / Mask 等非透明度四通道贴图，请注意根据需求调整 `Encoder.GetFlags`。

---

## 许可证

- 本项目代码采用 **MIT License**，详见 [Assets/ASTC Encoder/LICENSE.md](Assets/ASTC%20Encoder/LICENSE.md)。
- 底层编码库 [ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder) 采用 **Apache License 2.0**，详见 [Assets/ASTC Encoder/Third Party Notices.md](Assets/ASTC%20Encoder/Third%20Party%20Notices.md)。