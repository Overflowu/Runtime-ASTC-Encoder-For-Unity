# Changelog

All notable changes to `com.aperture.astcenc` (Runtime ASTC Encoder for Unity) are documented in this file.

## [2.3.0] - 2026-11-28

### Added
- **Zero-Readback GPU Mipmap Generation + ASTC Encoding**: New `AstcGpuMipGenerator.compute` (`DownsampleColor` with linear/sRGB-aware premultiplied-alpha box filtering, odd-NPOT edge handling, and HDR NaN/Inf sanitization; `DownsampleNormal` with unit-vector renormalization) integrated via `AstcCompressOptions.GenerateMipmaps` into a single `CommandBuffer` with zero-readback `CommandBuffer.CopyTexture` across aligned and non-multiple block dimensions and full mip chains.
- **Standard KTX v1 Container & Persistent Disk Cache**:
  - `AstcKtxContainer` (`Encode`, `TryDecode`, `LoadTexture`) + `Encoder.EncodeToKTX` / `EncodeToKTXAsync` / `LoadFromKTX` supporting full mipmap chains, exact UNorm/sRGB/HDR metadata, and automatic CPU decompression fallback on non-ASTC devices.
  - `AstcDiskCache` (`TryLoad`, `TryLoadAsync`, `CompressAndSaveAsync`, `LoadOrCompressAsync`) with atomic `File.Replace` persistence.
- **Windows x64 Runtime AVX2 Auto-Dispatch**: Ships both `astcenc-avx2-shared.dll` and `astcenc-sse4.1-shared.dll` in `Plugins/x64/`, automatically selecting AVX2 via `IsProcessorFeaturePresent(40)` with seamless SSE4.1 fallback.
- **Frame-Budgeted Compression Queue & Global CPU Context Pool**:
  - `AstcCompressionQueue` with `High`/`Normal`/`Low` priorities, configurable `MaxGpuTexelsPerFrame` & `MaxConcurrentCpuTasks`, single-`CommandBuffer` GPU batching per frame, and `CancellationToken` support.
  - Thread-safe `AstcContextPool` shared across all static CPU entry points with `parentContext` lookup-table sharing and `Encoder.ReleaseCpuContextPool()`.
- **Extended Texture Dimensions & Zero-Alloc Raw Pixel APIs**:
  - `sourceSlice` / `destSlice` parameters on `Encoder.Encode` for `Texture2DArray` and `Cubemap`, plus `Encoder.CompressCubemap` and `Encoder.CompressTexture2DArray`.
  - `Encoder.CompressRawPixels` / `CompressRawPixelsAsync` (`NativeArray<byte>` & `byte[]`) and zero-allocation `Encoder.CompressRawPixelsInto`.
- **Unity Job System Scheduling**: Optional `AstcCompressOptions.UseUnityJobSystem` / `AstcContext.UseUnityJobSystem` dispatching synchronous CPU compression slices on Unity's native `IJobParallelFor` Worker Thread Pool.
- **Editor Quality Inspector & Automated Benchmark Suite**:
  - `Window/ASTC Encoder/Benchmark & Inspector` (`Aperture.Astcenc.Editor`) comparing CPU vs GPU side-by-side with decoded previews, amplified error heatmaps, PSNR/RMSE, Normal Map mean/max angular error, timing, memory savings, and `.ktx`/`.astc` export.
  - `AstcPerformanceBenchmarkRunner` (`Window/ASTC Encoder/Run Performance Benchmark Suite`), EditMode `[Category("Performance")]` tests (`AstcPerformanceBenchmarks`), and standalone player benchmark pipeline.
- **Golden EditMode Test Suite & Multi-Platform CI**:
  - `Aperture.Astcenc.Tests` verifying `AstcHeader`, `AstcKtxContainer` round-trips, `CompressRawPixels`, `AstcContext` parent table sharing, `AstcCompressionQueue` cancellation, and CPU/GPU cross-decode PSNR & normal angular error.
  - Linux x64 P/Invoke (`UNITY_EDITOR_LINUX` / `UNITY_STANDALONE_LINUX`), GitHub Actions native plugin builds (Windows AVX2/SSE4.1, macOS Universal, iOS `astcenc.xcframework`, Android, Linux x64), HLSL & `.meta` GUID integrity verification, multi-platform GameCI E2E tests, and hardware performance benchmarks.

### Changed & Optimized
- **GPU Shader Throughput & Register Footprint**:
  - Per-thread packed `RGBA8` pixel cache for `<= 6x6` LDR blocks and per-block `ASTC_MAX_GRID` sizing to cut VGPR usage.
  - `[numthreads(8, 4, 1)]` occupancy tuning for `>= 8x8` blocks and zero-`pow()` fast `sqrt` sRGB approximation.
  - Row-level early-exit candidate pruning, same-grid weight projection reuse, combined 1P error + worst-channel accumulation, merged 3-pass 2-Partition endpoint fitting, and loop-hoisted `AstcHash52`.
- **UPM Package Hygiene**: Moved repository-only `CIBuildScript` out of the UPM package to `Assets/Editor/` so host projects do not receive extra `Build/CI/*` menu items.

### Fixed
- Canonicalized endpoints and flipped weights on endpoint swap during GPU candidate error evaluation, resolving PSNR degradation on `Medium`/`Thorough` and `6x6` blocks.
- Recognized 16-bit half-float (`R16G16B16A16_SFloat`) and UFloat HDR formats in the GPU backend and KTX software-decode fallback.
- Fixed Unity 2021.3 Metal shader compilation failure involving `reversebits`.
- Fixed NPOT odd-dimension mip filtering, guarded HDR mip generation on FP16 `LoadStore` support, and fixed HDR readable copy & GGGR normal-map unpacking in `AstcBenchmarkWindow`.
- Fixed `Cubemap` constructor argument order, `parentContext` use-after-free, post-await texture access, and `AstcRaw` leak in `AstcCompressionQueue`.
- Hardened `AstcKtxContainer.TryDecode`, `CompressRawPixelsAsync`, `CompressRawPixelsInto`, and decompression bitness/bounds/slice validation against integer overflows, malformed headers, and redundant allocations.

## [2.0.0] - 2026-11-26

### Added
- **Unified Dual-Backend (`Cpu` / `Gpu` / `Auto`)**: Custom tableless HLSL Compute Shader ASTC encoder supporting `4x4`..`12x12`, LDR/sRGB/Normal/HDR (`CEM 11/14/15`), `ASTC_FAST_ONLY` kernels, dynamic-CCS Dual-Plane, anisotropic grids, angular-error normal encoding, post-quantization endpoint refit, and zero-readback `CommandBuffer.CopyTexture`.
- **CPU `astc-encoder v5.7.1`**: Software decompression (`Encoder.Decompress*`), `parentContext` lookup-table sharing, `UsePerceptual`, `AlphaScaleRadius`, compact async snapshots, and `AstcCompressOptions`.
