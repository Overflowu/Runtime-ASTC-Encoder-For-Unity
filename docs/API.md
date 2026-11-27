# API Reference

All public types reside in the `Aperture.Astcenc.Runtime` namespace.

---

## `Encoder` — Unified Entry Point

All compression methods accept `EncodeBackend` (default `EncodeBackend.Auto`: prefers GPU when supported, falls back to CPU automatically) and also provide `AstcCompressOptions` struct overloads.

### Capability & Lifecycle

| Method / Property | Description |
| :--- | :--- |
| `Encoder.IsCpuSupported` | Returns `true` if a native `astc-encoder` binary is available on the current platform |
| `Encoder.IsGpuSupported` | Returns `true` if the device supports Compute Shaders and `R32G32B32A32_UInt` UAV writes |
| `Encoder.SupportsGpuDirectCopy(blockSize, srgb, isHdr)` | Checks whether the GPU supports zero-readback `CopyTexture` into the target ASTC format |
| `Encoder.IsSupport(Texture texture, EncodeBackend backend = EncodeBackend.Auto)` | Validates texture compatibility for the given backend (GPU accepts non-readable `Texture2D` and non-MSAA `RenderTexture`) |
| `Encoder.GetTargetAstcFormat(sourceFormat, blockSize, textureType)` | Resolves the target ASTC `GraphicsFormat` (`TextureType.Normal` always resolves to linear `_UNorm`) |
| `Encoder.PreloadGpu(blockSize)` | Warms up Fast/Full GPU encoder kernels and the GPU mipmap generator |
| `Encoder.ReleaseGpuCache()` | Releases cached Compute Shader references |
| `Encoder.ReleaseCpuContextPool()` | Releases all cached native `AstcContext` instances held by the static CPU pool |

### Compression & Encoding

| Method | Description |
| :--- | :--- |
| `Texture.CompressToTexture(..., backend = EncodeBackend.Auto)` | Compresses a 2D `Texture` / `RenderTexture` into a newly allocated ASTC `Texture2D`. Uses zero-readback `CopyTexture` on GPU (including non-multiple dimensions, full mip chains, and `AstcCompressOptions.GenerateMipmaps`) |
| `Texture.CompressToTextureAsync(..., backend = EncodeBackend.Auto)` | Non-blocking version using `AsyncGPUReadback` or background CPU worker threads (supports `CancellationToken`) |
| `Texture2D.Compress(..., backend = EncodeBackend.Auto)` | In-place compression via `Reinitialize` (requires `isReadable == true`; uses GPU zero-readback when `makeNoLongerReadable == true`) |
| `Encoder.Encode(CommandBuffer cmd, source, destination, ...)` | Records GPU ASTC encoding + `CopyTexture` into a `CommandBuffer` (`sourceSlice` / `destSlice`, `sourceMip` / `destMip`, block-aligned `destX` / `destY`) for batching, Async Compute, or Virtual Texture / atlas updates |
| `Texture.CompressTexture(...)` / `CompressTextureAsync(...)` | Compresses all mip levels into unmanaged memory and returns `AstcRaw` (caller must `Dispose()`) |
| `Encoder.CompressCubemap(...)` / `CompressTexture2DArray(...)` | Compresses multi-slice `Cubemap` or `Texture2DArray` assets |
| `Encoder.CompressRawPixels(...)` / `CompressRawPixelsAsync(...)` | Compresses raw `NativeArray<byte>` or `byte[]` pixel buffers directly on CPU |
| `Encoder.CompressRawPixelsInto(..., NativeArray<byte> destination)` | Zero-allocation CPU compression directly into a caller-owned `NativeArray<byte>` (uses pooled `AstcContext`) |
| `Texture.EncodeToASTC(...)` / `EncodeToASTCAsync(...)` | Encodes mip 0 only and prepends the standard 16-byte `.astc` file header, returning `byte[]` |
| `Texture.EncodeToKTX(...)` / `EncodeToKTXAsync(...)` | Encodes all mip levels into a standard KTX v1 container (`byte[]`) preserving UNorm/sRGB/HDR metadata |

### Decoding & Loading

| Method | Description |
| :--- | :--- |
| `Texture2D.LoadRawTextureData(AstcRaw raw)` | Uploads unmanaged `AstcRaw` directly into a `Texture2D` without managed copies |
| `Encoder.LoadFromKTX(byte[] ktxData, ...)` | Parses a KTX v1 container and creates an ASTC `Texture2D` (automatically falls back to CPU software decompression on non-ASTC devices) |
| `Encoder.Decompress(...)` / `DecompressToTexture(...)` / `DecompressAstcFile(...)` | CPU software decompression of raw ASTC blocks or `.astc` file bytes via native `astc-encoder` |

---

## High-Level Production Utilities

### `AstcCompressionQueue`
Frame-budgeted scheduler for runtime streaming and UGC pipelines:
- Priority queues (`High`, `Normal`, `Low`)
- `MaxGpuTexelsPerFrame` GPU budget and `MaxConcurrentCpuTasks` concurrency cap
- Single-`CommandBuffer` per-frame GPU batching
- Pooled native `AstcContext` with safe `parentContext` table sharing

### `AstcKtxContainer` & `AstcDiskCache`
- **`AstcKtxContainer`**: Reads and writes standard KTX v1 files with full mipmap chains and color-space/HDR metadata.
- **`AstcDiskCache`**: Persistent on-disk cache using atomic `File.Replace` writes and zero-copy `AstcRaw` uploads.

---

## `AstcContext` (Explicit Reusable CPU Context)

All static CPU `Encoder` APIs already reuse pooled native contexts automatically via `AstcContextPool`. Use `AstcContext` directly when you want explicit lifetime ownership or custom `parentContext` sharing across workers:

| Member | Description |
| :--- | :--- |
| `AstcContext.Create(format, textureType, blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, parentContext)` | Allocates a reusable compression context (`threadCount <= 0` auto-selects worker threads) |
| `AstcContext.CreateDecompressor(blockSize, srgb, isHdr, threadCount)` | Allocates a lightweight `DecompressOnly` context for CPU software decoding |
| `context.CompressTexture(texture, textureType)` | Synchronously compresses using this context |
| `context.CompressTextureAsync(texture, textureType, cancellationToken)` | Asynchronously compresses using this context (keep `context` alive until the `Task` completes) |
| `context.Dispose()` | Thread-safely frees the native `astcenc_context` |

---

## `AstcRaw` (Unmanaged Compressed Buffer)

- `Length`: Total compressed byte count
- `IsDisposed`: Whether the buffer has been freed
- `CopyTo(byte[] destination, int destinationOffset = 0)` / `ToArray()`: Copies bytes into a managed array
- `Dispose()`: Thread-safely frees unmanaged memory (always use `using`)

---

## Configuration & Enums

- **`AstcCompressOptions`**: Unified struct with `Default`, `NormalMap`, and `Transparent` presets; exposes `GenerateMipmaps`, `UseAlphaWeight`, `UsePerceptual`, `AlphaScaleRadius`, `UseUnityJobSystem`, and `ThreadCount`.
- **`EncodeBackend`**: `Auto` (default), `Gpu`, `Cpu`.
- **`TextureType`**: `Default` (color / ORM), `Normal` (`GGGR` swizzle, always linear `_UNorm` output).
- **`BlockSize`** (16 bytes per block, all 6 supported on both CPU and GPU):
  - `ASTC_4x4` (8.00 bpp) · `ASTC_5x5` (5.12 bpp) · `ASTC_6x6` (3.56 bpp) · `ASTC_8x8` (2.00 bpp) · `ASTC_10x10` (1.28 bpp) · `ASTC_12x12` (0.89 bpp)
- **`CompressQuality`**:
  - **CPU**: `Fastest` (`0`), `Fast` (`10`), `Medium` (`60`), `Thorough` (`98`), `VeryThorough` (`99`), `Exhaustive` (`100`)
  - **GPU**: `Fastest`/`Fast` → lightweight `ASTC_FAST_ONLY` kernel; `Medium` → multi-BlockMode + anisotropic grids + dynamic-CCS Dual-Plane + post-quantization endpoint refit + HDR CEM 11 delta modes; `Thorough`–`Exhaustive` → adds 16-seed 2-Partition search
- **`CodecFlags`**: Maps to `astcenc 5.7.1` (`MapNormal`, `UseDecodeUnorm8`, `UseAlphaWeight`, `UsePerceptual`, `DecompressOnly`, `SelfDecompressOnly`, `MapRGBM`).

---

## Supported Input Formats & Target ASTC Mapping

| Input `GraphicsFormat` Category | Supported Formats | `astcenc` Profile | Internal Transfer | Target ASTC `GraphicsFormat` |
| :--- | :--- | :--- | :--- | :--- |
| **LDR 8-bit sRGB** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_SRGB`) | `LDR_SRGB` (`LDR` for Normal) | `U8` (4-ch zero-copy) | `RGBA_ASTC{N}X{N}_SRGB` (`_UNorm` for Normal) |
| **LDR 8-bit Linear** | `R8` / `R8G8` / `R8G8B8` / `R8G8B8A8` / `B8G8R8` / `B8G8R8A8` (`_UNorm`) | `LDR` | `U8` (4-ch zero-copy) | `RGBA_ASTC{N}X{N}_UNorm` |
| **LDR 16-bit Linear** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_UNorm`) | `LDR` | `F32` (preserves 16-bit precision) | `RGBA_ASTC{N}X{N}_UNorm` |
| **HDR 16-bit Float** | `R16` / `R16G16` / `R16G16B16` / `R16G16B16A16` (`_SFloat`) | `HDR` | `F16` (4-ch zero-copy) | `RGBA_ASTC{N}X{N}_UFloat` |
| **HDR 32-bit Float** | `R32` / `R32G32` / `R32G32B32` / `R32G32B32A32` (`_SFloat`) | `HDR` | `F32` (4-ch zero-copy) | `RGBA_ASTC{N}X{N}_UFloat` |

> The GPU backend also supports additional 2D `RenderTexture` formats such as `B10G11R11_UFloatPack32`.

---

## Advanced Examples

### Zero-Allocation Raw Pixel Compression (CPU)

```csharp
using Aperture.Astcenc.Runtime;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;

public int CompressBufferZeroAlloc(
    NativeArray<byte> rgbaPixels,
    int width,
    int height,
    NativeArray<byte> astcOutput)
{
    return Encoder.CompressRawPixelsInto(
        rgbaPixels,
        width,
        height,
        GraphicsFormat.R8G8B8A8_SRGB,
        astcOutput,
        TextureType.Default,
        BlockSize.ASTC_6x6,
        CompressQuality.Fast);
}
```

### KTX v1 Export & Persistent Disk Cache

```csharp
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using UnityEngine;

public async Task<Texture2D> EncodeAndCacheAsync(Texture2D src, string cacheKey, AstcDiskCache cache)
{
    if (cache.TryLoad(cacheKey, out Texture2D cached))
        return cached;

    byte[] ktx = await src.EncodeToKTXAsync(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast);
    if (ktx != null)
        cache.Save(cacheKey, ktx);

    return ktx != null ? Encoder.LoadFromKTX(ktx) : null;
}
```

### Standard `.astc` File Export (Mip 0)

```csharp
using System.IO;
using Aperture.Astcenc.Runtime;
using UnityEngine;

public void SaveAstcFile(Texture2D src, string path)
{
    byte[] bytes = src.EncodeToASTC(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Medium);
    if (bytes != null)
        File.WriteAllBytes(path, bytes);
}
```
