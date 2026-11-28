# Runtime ASTC Encoder (`com.aperture.astcenc`)

High-performance runtime ASTC texture compression package for Unity **2021.3+**, combining a **real-time GPU Compute Shader encoder** and a **native CPU encoder** (ARM `astc-encoder v5.7.1`) behind a unified `Aperture.Astcenc.Runtime.Encoder` API.

## Quick Start

```csharp
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using UnityEngine;

// 1. One-shot compression (Auto: zero-readback GPU CopyTexture when supported, CPU fallback otherwise)
Texture2D compressed = sourceTexture.CompressToTexture(
    textureType: TextureType.Default,
    blockSize: BlockSize.ASTC_6x6,
    quality: CompressQuality.Fast,
    includeMipmaps: true);

// 2. Non-blocking async compression (recommended on mobile)
Texture2D compressedAsync = await sourceTexture.CompressToTextureAsync(
    textureType: TextureType.Default,
    blockSize: BlockSize.ASTC_6x6,
    quality: CompressQuality.Fast,
    includeMipmaps: true);

// 3. Single-CommandBuffer GPU mipmap generation + zero-readback ASTC encoding
var options = AstcCompressOptions.Default;
options.BlockSize = BlockSize.ASTC_6x6;
options.GenerateMipmaps = true;
options.Backend = EncodeBackend.Gpu;
Texture2D compressedWithMips = renderTexture.CompressToTexture(options);
```

## Included Features

- **Unified Dual-Backend (`Auto` / `Gpu` / `Cpu`)**: Supports `4x4`, `5x5`, `6x6`, `8x8`, `10x10`, and `12x12` across LDR, sRGB, Normal (`GGGR`), and HDR (`CEM 11/14/15`).
- **Zero-Readback GPU Pipeline**: Direct `CommandBuffer.CopyTexture` encoding for `Texture2D`, `RenderTexture`, `Cubemap`, and `Texture2DArray`, including optional GPU mipmap generation.
- **KTX v1 Container & Disk Cache**: `AstcKtxContainer` and `AstcDiskCache` (`TryLoad`, `TryLoadAsync`, `CompressAndSaveAsync`, `LoadOrCompressAsync`) with atomic `File.Replace` persistence.
- **Production Utilities**: Frame-budgeted `AstcCompressionQueue`, pooled `AstcContextPool`, zero-alloc `Encoder.CompressRawPixelsInto`, Unity Job System (`IJobParallelFor`) scheduling, and CPU software decompression.
- **Editor Tools**:
  - **Window → ASTC Encoder → Benchmark & Inspector**
  - **Window → ASTC Encoder → Run Performance Benchmark Suite**
- **Samples**: Import **Basic Usage** from **Package Manager → Runtime ASTC Encoder → Samples**.

## Documentation & Links

- [Repository & Full README](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity)
- [API Reference](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/blob/master/docs/API.md)
- [Architecture & Implementation](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/blob/master/docs/Architecture.md)
- [Changelog](CHANGELOG.md)
- [License (MIT)](LICENSE.md) & [Third Party Notices](Third%20Party%20Notices.md)
