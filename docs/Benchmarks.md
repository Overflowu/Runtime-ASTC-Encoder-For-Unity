# Runtime ASTC Encoder — Performance Benchmarks & Regression Baseline

- **Updated**: `2026-11-28 14:45 UTC` | **Commit**: `d3223a5` | [GitHub Actions Run](https://github.com/Overflowu/Runtime-ASTC-Encoder-For-Unity/actions/runs/46153027417)
- **Baseline Comparison**: Deltas (`Δ vs Baseline`) compare each benchmark's median latency against the previous saved snapshot in `docs/benchmarks/baseline.json` (`🚀` ≥ 5% faster, `⚠️` ≥ 15% slower).

## 1. Unity Standalone Player (Real Hardware GPU + CPU)

- **GPU**: `Apple Paravirtual device` (`Metal`)
- **CPU**: `Apple M1 (Virtual)` (3 cores)
- **Unity**: `2021.3.6f1` | **Platform**: `OSXPlayer`
- **GPU Direct CopyTexture**: `4x4=True`, `6x6=True`

| Benchmark | Backend | Res | Block | Quality | Min (ms) | Median (ms) | P95 (ms) | MP/s | GC (KB) | RGB PSNR | Normal Err | Δ vs Baseline |
| :--- | :--- | :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `GPU_RGBA8_1024_ASTC_4x4_Fast` | GPU | 1024x1024 | ASTC_4x4 | Fast | 7.26 | **7.66** | 8.60 | 136.94 | 0.0 | 48.94 dB | — | new |
| `CPU_RGBA8_1024_ASTC_4x4_Fast` | CPU | 1024x1024 | ASTC_4x4 | Fast | 233.97 | **253.41** | 281.95 | 4.14 | 8.0 | 48.95 dB | — | new |
| `GPU_RGBA8_1024_ASTC_4x4_Medium` | GPU | 1024x1024 | ASTC_4x4 | Medium | 40.40 | **41.00** | 41.14 | 25.58 | 0.0 | 46.44 dB | — | new |
| `CPU_RGBA8_1024_ASTC_4x4_Medium` | CPU | 1024x1024 | ASTC_4x4 | Medium | 444.33 | **519.59** | 825.93 | 2.02 | 7.0 | 50.24 dB | — | new |
| `GPU_RGBA8_1024_ASTC_6x6_Fast` | GPU | 1024x1024 | ASTC_6x6 | Fast | 13.73 | **20.34** | 23.50 | 51.55 | 0.0 | 29.27 dB | — | new |
| `CPU_RGBA8_1024_ASTC_6x6_Fast` | CPU | 1024x1024 | ASTC_6x6 | Fast | 355.64 | **371.75** | 395.72 | 2.82 | 4.0 | 41.75 dB | — | new |
| `GPU_RGBA8_1024_ASTC_6x6_Medium` | GPU | 1024x1024 | ASTC_6x6 | Medium | 54.40 | **57.49** | 60.93 | 18.24 | 0.0 | 32.57 dB | — | new |
| `CPU_RGBA8_1024_ASTC_6x6_Medium` | CPU | 1024x1024 | ASTC_6x6 | Medium | 824.27 | **876.38** | 954.63 | 1.20 | 4.0 | 43.23 dB | — | new |
| `GPU_RGBA8_1024_ASTC_8x8_Fast` | GPU | 1024x1024 | ASTC_8x8 | Fast | 8.18 | **8.51** | 9.22 | 123.15 | 0.0 | 43.43 dB | — | new |
| `CPU_RGBA8_1024_ASTC_8x8_Fast` | CPU | 1024x1024 | ASTC_8x8 | Fast | 757.78 | **836.19** | 1017.42 | 1.25 | 3.0 | 45.99 dB | — | new |
| `GPU_RGBA8_1024_ASTC_8x8_Medium` | GPU | 1024x1024 | ASTC_8x8 | Medium | 36.27 | **37.02** | 40.93 | 28.32 | 0.8 | 44.05 dB | — | new |
| `CPU_RGBA8_1024_ASTC_8x8_Medium` | CPU | 1024x1024 | ASTC_8x8 | Medium | 1685.85 | **1848.60** | 2193.39 | 0.57 | 4.0 | 47.93 dB | — | new |
| `GPU_RGBA8_1024_ASTC_12x12_Fast` | GPU | 1024x1024 | ASTC_12x12 | Fast | 8.38 | **10.85** | 11.44 | 96.63 | 0.0 | 24.67 dB | — | new |
| `CPU_RGBA8_1024_ASTC_12x12_Fast` | CPU | 1024x1024 | ASTC_12x12 | Fast | 618.65 | **710.89** | 795.81 | 1.48 | 4.0 | 27.78 dB | — | new |
| `GPU_RGBA8_1024_ASTC_12x12_Medium` | GPU | 1024x1024 | ASTC_12x12 | Medium | 29.09 | **30.73** | 32.13 | 34.12 | 0.0 | 25.34 dB | — | new |
| `CPU_RGBA8_1024_ASTC_12x12_Medium` | CPU | 1024x1024 | ASTC_12x12 | Medium | 1095.57 | **1254.79** | 1345.90 | 0.84 | 4.0 | 30.08 dB | — | new |
| `GPU_RGBA8_512_ASTC_4x4_Fast` | GPU | 512x512 | ASTC_4x4 | Fast | 3.85 | **5.55** | 7.34 | 47.27 | 0.0 | 44.54 dB | — | new |
| `CPU_RGBA8_512_ASTC_4x4_Fast` | CPU | 512x512 | ASTC_4x4 | Fast | 115.25 | **148.60** | 168.08 | 1.76 | 3.2 | 44.51 dB | — | new |
| `GPU_RGBA8_512_ASTC_4x4_Medium` | GPU | 512x512 | ASTC_4x4 | Medium | 20.40 | **20.59** | 21.44 | 12.73 | 0.0 | 43.86 dB | — | new |
| `CPU_RGBA8_512_ASTC_4x4_Medium` | CPU | 512x512 | ASTC_4x4 | Medium | 254.42 | **277.40** | 387.44 | 0.95 | 3.2 | 47.81 dB | — | new |
| `GPU_RGBA8_512_ASTC_4x4_Thorough` | GPU | 512x512 | ASTC_4x4 | Thorough | 14.30 | **14.94** | 18.69 | 17.54 | 0.0 | 40.17 dB | — | new |
| `CPU_RGBA8_512_ASTC_4x4_Thorough` | CPU | 512x512 | ASTC_4x4 | Thorough | 542.45 | **593.42** | 639.67 | 0.44 | 4.0 | 51.62 dB | — | new |
| `GPU_RGBA8_512_ASTC_6x6_Fast` | GPU | 512x512 | ASTC_6x6 | Fast | 4.02 | **4.36** | 10.73 | 60.19 | 0.0 | 28.59 dB | — | new |
| `CPU_RGBA8_512_ASTC_6x6_Fast` | CPU | 512x512 | ASTC_6x6 | Fast | 136.68 | **147.53** | 157.66 | 1.78 | 3.2 | 37.20 dB | — | new |
| `GPU_RGBA8_512_ASTC_6x6_Medium` | GPU | 512x512 | ASTC_6x6 | Medium | 15.62 | **16.58** | 24.03 | 15.81 | 0.0 | 30.75 dB | — | new |
| `CPU_RGBA8_512_ASTC_6x6_Medium` | CPU | 512x512 | ASTC_6x6 | Medium | 303.94 | **321.06** | 564.12 | 0.82 | 3.2 | 38.69 dB | — | new |
| `GPU_RGBA8_512_ASTC_6x6_Thorough` | GPU | 512x512 | ASTC_6x6 | Thorough | 18.58 | **20.09** | 22.55 | 13.05 | 0.0 | 31.71 dB | — | new |
| `CPU_RGBA8_512_ASTC_6x6_Thorough` | CPU | 512x512 | ASTC_6x6 | Thorough | 668.67 | **703.41** | 732.16 | 0.37 | 4.0 | 39.10 dB | — | new |
| `GPU_RGBA8_512_ASTC_8x8_Fast` | GPU | 512x512 | ASTC_8x8 | Fast | 3.46 | **3.52** | 5.65 | 74.55 | 0.0 | 37.82 dB | — | new |
| `CPU_RGBA8_512_ASTC_8x8_Fast` | CPU | 512x512 | ASTC_8x8 | Fast | 175.76 | **229.24** | 260.96 | 1.14 | 3.2 | 41.99 dB | — | new |
| `GPU_RGBA8_512_ASTC_8x8_Medium` | GPU | 512x512 | ASTC_8x8 | Medium | 11.04 | **11.84** | 18.64 | 22.15 | 0.0 | 39.45 dB | — | new |
| `CPU_RGBA8_512_ASTC_8x8_Medium` | CPU | 512x512 | ASTC_8x8 | Medium | 327.95 | **373.93** | 389.11 | 0.70 | 3.2 | 43.14 dB | — | new |
| `GPU_RGBA8_512_ASTC_8x8_Thorough` | GPU | 512x512 | ASTC_8x8 | Thorough | 12.09 | **12.41** | 13.36 | 21.12 | 0.0 | 39.08 dB | — | new |
| `CPU_RGBA8_512_ASTC_8x8_Thorough` | CPU | 512x512 | ASTC_8x8 | Thorough | 631.27 | **675.98** | 762.20 | 0.39 | 4.0 | 47.48 dB | — | new |
| `GPU_RGBA8_512_ASTC_12x12_Fast` | GPU | 512x512 | ASTC_12x12 | Fast | 2.30 | **2.32** | 2.42 | 112.95 | 0.0 | 24.04 dB | — | new |
| `CPU_RGBA8_512_ASTC_12x12_Fast` | CPU | 512x512 | ASTC_12x12 | Fast | 136.28 | **158.24** | 171.77 | 1.66 | 3.2 | 26.31 dB | — | new |
| `GPU_RGBA8_512_ASTC_12x12_Medium` | GPU | 512x512 | ASTC_12x12 | Medium | 11.70 | **20.36** | 20.61 | 12.88 | 0.0 | 24.34 dB | — | new |
| `CPU_RGBA8_512_ASTC_12x12_Medium` | CPU | 512x512 | ASTC_12x12 | Medium | 250.49 | **308.17** | 369.28 | 0.85 | 3.2 | 27.73 dB | — | new |
| `GPU_RGBA8_512_ASTC_12x12_Thorough` | GPU | 512x512 | ASTC_12x12 | Thorough | 10.13 | **10.33** | 12.98 | 25.39 | 0.0 | 24.29 dB | — | new |
| `CPU_RGBA8_512_ASTC_12x12_Thorough` | CPU | 512x512 | ASTC_12x12 | Thorough | 519.84 | **558.45** | 592.55 | 0.47 | 4.0 | 27.42 dB | — | new |
| `GPU_ZeroReadback_1024_6x6_Fast` | GPU | 1024x1024 | ASTC_6x6 | Fast | 0.54 | **2.70** | 3.75 | 388.68 | 0.0 | — | — | new |
| `GPU_ZeroReadback_MipGen_1024_6x6_Fast` | GPU | 1024x1024+Mips | ASTC_6x6 | Fast | 0.66 | **0.75** | 1.63 | 1861.65 | 0.0 | — | — | new |
| `GPU_Normal_512_6x6_Medium` | GPU | 512x512 | ASTC_6x6 | Medium | 12.36 | **19.26** | 23.55 | 13.61 | 0.0 | — | 0.27° | new |
| `CPU_Normal_512_6x6_Medium` | CPU | 512x512 | ASTC_6x6 | Medium | 294.23 | **319.21** | 377.23 | 0.82 | 4.0 | — | 0.15° | new |
| `GPU_sRGB_512_6x6_Fast` | GPU | 512x512 | ASTC_6x6 | Fast | 4.26 | **6.57** | 10.52 | 39.88 | 0.0 | — | — | new |
| `GPU_HDR16F_512_6x6_Fast` | GPU | 512x512 | ASTC_6x6 | Fast | 0.01 | **0.01** | 0.02 | 23831.27 | 0.0 | — | — | new |
| `CPU_CompressRawPixels_512_6x6_Fast` | CPU | 512x512 | ASTC_6x6 | Fast | 171.83 | **181.33** | 191.89 | 1.45 | 4.0 | — | — | new |
| `CPU_CompressRawPixelsInto_512_6x6_Fast` | CPU | 512x512 | ASTC_6x6 | Fast | 180.29 | **196.09** | 216.09 | 1.34 | 4.0 | — | — | new |
| `CPU_Decompress_512_6x6_3T` | CPU | 512x512 | ASTC_6x6 | N/A | 1.29 | **1.55** | 7.97 | 169.35 | 416.0 | — | — | new |
| `DiskCache_TryLoad_512_6x6_FullMips` | Disk+GPU | 512x512+Mips | ASTC_6x6 | N/A | 0.33 | **0.66** | 0.98 | 529.18 | 124.8 | — | — | new |

## 2. Cross-Platform Native CPU (`astc-encoder v5.7.1`)

| Platform / CPU | Benchmark | ISA | Threads | Min (ms) | Median (ms) | P95 (ms) | MP/s | Δ vs Baseline |
| :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_4x4_Fast_4T` | `AVX2` | 4 | 58.76 | **58.87** | 66.53 | 4.45 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_4x4_Medium_4T` | `AVX2` | 4 | 180.32 | **191.24** | 192.53 | 1.37 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_6x6_Fast_4T` | `AVX2` | 4 | 39.97 | **40.04** | 40.25 | 6.55 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_6x6_Medium_4T` | `AVX2` | 4 | 172.89 | **173.18** | 186.41 | 1.51 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_8x8_Fast_4T` | `AVX2` | 4 | 31.29 | **31.44** | 40.67 | 8.34 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_8x8_Medium_4T` | `AVX2` | 4 | 87.35 | **87.52** | 98.31 | 3.00 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_12x12_Fast_4T` | `AVX2` | 4 | 69.79 | **70.21** | 73.02 | 3.73 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_512_ASTC_12x12_Medium_4T` | `AVX2` | 4 | 297.92 | **308.52** | 311.08 | 0.85 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_AVX2_Decompress_512_ASTC_6x6_4T` | `AVX2` | 4 | 1.24 | **1.28** | 1.37 | 204.85 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_4x4_Fast_4T` | `SSE4.1` | 4 | 64.67 | **65.23** | 70.17 | 4.02 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_4x4_Medium_4T` | `SSE4.1` | 4 | 193.89 | **200.34** | 206.89 | 1.31 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_6x6_Fast_4T` | `SSE4.1` | 4 | 43.82 | **43.90** | 44.26 | 5.97 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_6x6_Medium_4T` | `SSE4.1` | 4 | 180.35 | **180.55** | 192.34 | 1.45 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_8x8_Fast_4T` | `SSE4.1` | 4 | 33.93 | **34.05** | 43.32 | 7.70 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_8x8_Medium_4T` | `SSE4.1` | 4 | 92.35 | **93.42** | 103.63 | 2.81 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_12x12_Fast_4T` | `SSE4.1` | 4 | 69.49 | **69.66** | 81.05 | 3.76 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_512_ASTC_12x12_Medium_4T` | `SSE4.1` | 4 | 283.73 | **296.89** | 297.93 | 0.88 | new |
| Windows AMD64 (`AMD EPYC 9V74 80-Core Processor`) | `Native_SSE4.1_Decompress_512_ASTC_6x6_4T` | `SSE4.1` | 4 | 1.84 | **1.87** | 1.99 | 140.04 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_4x4_Fast_3T` | `ARM64_NEON` | 3 | 61.30 | **73.72** | 89.83 | 3.56 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_4x4_Medium_3T` | `ARM64_NEON` | 3 | 155.97 | **186.31** | 248.39 | 1.41 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_6x6_Fast_3T` | `ARM64_NEON` | 3 | 34.40 | **34.68** | 35.09 | 7.56 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_6x6_Medium_3T` | `ARM64_NEON` | 3 | 144.50 | **154.93** | 202.13 | 1.69 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_8x8_Fast_3T` | `ARM64_NEON` | 3 | 27.69 | **28.03** | 28.47 | 9.35 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_8x8_Medium_3T` | `ARM64_NEON` | 3 | 71.27 | **71.79** | 90.55 | 3.65 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_12x12_Fast_3T` | `ARM64_NEON` | 3 | 54.55 | **55.24** | 57.33 | 4.75 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_512_ASTC_12x12_Medium_3T` | `ARM64_NEON` | 3 | 198.91 | **232.17** | 246.13 | 1.13 | new |
| Darwin arm64 (`Apple M1 (Virtual)`) | `Native_ARM64_NEON_Decompress_512_ASTC_6x6_3T` | `ARM64_NEON` | 3 | 1.38 | **1.51** | 2.27 | 173.77 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_4x4_Fast_4T` | `SSE4.1` | 4 | 73.24 | **73.32** | 73.77 | 3.58 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_4x4_Medium_4T` | `SSE4.1` | 4 | 220.29 | **220.75** | 222.05 | 1.19 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_6x6_Fast_4T` | `SSE4.1` | 4 | 50.00 | **50.48** | 51.02 | 5.19 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_6x6_Medium_4T` | `SSE4.1` | 4 | 196.73 | **197.06** | 203.91 | 1.33 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_8x8_Fast_4T` | `SSE4.1` | 4 | 37.80 | **37.84** | 37.94 | 6.93 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_8x8_Medium_4T` | `SSE4.1` | 4 | 98.53 | **98.71** | 100.25 | 2.66 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_12x12_Fast_4T` | `SSE4.1` | 4 | 74.94 | **75.14** | 75.41 | 3.49 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_512_ASTC_12x12_Medium_4T` | `SSE4.1` | 4 | 298.12 | **299.78** | 301.03 | 0.87 | new |
| Linux x86_64 (`AMD EPYC 7763 64-Core Processor`) | `Native_SSE4.1_Decompress_512_ASTC_6x6_4T` | `SSE4.1` | 4 | 2.15 | **2.18** | 2.21 | 120.44 | new |
