# Changelog

All notable changes to `com.aperture.astcenc` (Runtime ASTC Encoder for Unity) are documented in this file.

## [2.3.0] - 2026-11-26

### Added
- **GPU Mipmap Generation + ASTC Compression All-in-One**: New `AstcGpuMipGenerator.compute` (`DownsampleColor` with linear/sRGB-aware premultiplied-alpha box filtering and HDR NaN/Inf sanitization; `DownsampleNormal` with unit-vector renormalization) integrated via `AstcCompressOptions.GenerateMipmaps`.
- **Standard KTX v1 Container & Persistent Disk Cache**:
  - `AstcKtxContainer` (`Encode`, `TryDecode`, `LoadTexture`) + `Encoder.EncodeToKTX` / `EncodeToKTXAsync` / `LoadFromKTX` supporting full mipmap chains, exact UNorm/sRGB/HDR metadata, and automatic CPU decompression fallback on non-ASTC devices.
  - `AstcDiskCache` (`TryLoad`, `TryLoadAsync`, `CompressAndSaveAsync`, `LoadOrCompressAsync`) with atomic temp-file-rename persistence.
- **Unity Editor Visual Benchmark & Quality Inspector**: `Window/ASTC Encoder/Benchmark & Inspector` (`Aperture.Astcenc.Editor`) comparing CPU vs GPU side-by-side with decoded previews, amplified error heatmaps, PSNR/RMSE, Normal Map mean/max angular error, timing, memory savings, and `.ktx`/`.astc` export.
- **GPU On-Chip Packed Pixel Cache**: Per-thread packed RGBA8 cache for `<= 6x6` LDR blocks in `AstcGpuCore.hlsl`, eliminating redundant texture loads and sRGB `pow()` calls across multi-pass search.
- **Frame-Budgeted Compression Queue & Context Pool**: `AstcCompressionQueue` with `High`/`Normal`/`Low` priorities, configurable `MaxGpuTexelsPerFrame` & `MaxConcurrentCpuTasks`, `CancellationToken`, and pooled `AstcContext` with `parentContext` table sharing.
- **Extended Texture Dimensions & Raw Pixel APIs**:
  - `sourceSlice` / `destSlice` parameters on `Encoder.Encode` for `Texture2DArray` and `Cubemap`.
  - `Encoder.CompressCubemap` and `Encoder.CompressTexture2DArray` zero-readback GPU helpers.
  - `Encoder.CompressRawPixels` / `CompressRawPixelsAsync` (`NativeArray<byte>` & `byte[]`) for compressing raw pixel buffers without creating a `Texture2D`.
- **Unity Job System Scheduling**: Optional `AstcCompressOptions.UseUnityJobSystem` / `AstcContext.UseUnityJobSystem` dispatching synchronous CPU compression slices on Unity's native `IJobParallelFor` Worker Thread Pool.
- **Golden EditMode Test Suite**: `Aperture.Astcenc.Tests` verifying `AstcHeader`, `AstcKtxContainer` round-trips, `CompressRawPixels`, `AstcContext` parent table sharing, `AstcCompressionQueue` cancellation, and CPU/GPU cross-decode PSNR & normal angular error.
- **CI & Platform Enhancements**: Added Linux x64 P/Invoke (`UNITY_EDITOR_LINUX` / `UNITY_STANDALONE_LINUX`), GitHub Actions builds for Linux x64, Windows AVX2, and iOS `astcenc.xcframework` (Device + Simulator), plus automated HLSL & `.meta` GUID integrity verification workflow.

## [2.0.0] - 2026-11-26

### Added
- **Unified Dual-Backend (`Cpu` / `Gpu` / `Auto`)**: Custom tableless HLSL Compute Shader ASTC encoder supporting `4x4`..`12x12`, LDR/sRGB/Normal/HDR (`CEM 11/14/15`), `ASTC_FAST_ONLY` kernels, dynamic-CCS Dual-Plane, anisotropic grids, angular-error normal encoding, post-quantization endpoint refit, and zero-readback `CommandBuffer.CopyTexture`.
- **CPU `astc-encoder v5.7.1`**: Software decompression (`Encoder.Decompress*`), `parentContext` lookup-table sharing, `UsePerceptual`, `AlphaScaleRadius`, compact async snapshots, and `AstcCompressOptions`.
