using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Aperture.Astcenc.Runtime
{
    public static class Encoder
    {
        internal static bool GetBlockSize(BlockSize size, out uint x, out uint y, out uint z)
        {
            switch (size)
            {
                case BlockSize.ASTC_4x4:
                    x = y = 4;
                    z = 1;
                    return true;
                case BlockSize.ASTC_5x5:
                    x = y = 5;
                    z = 1;
                    return true;
                case BlockSize.ASTC_6x6:
                    x = y = 6;
                    z = 1;
                    return true;
                case BlockSize.ASTC_8x8:
                    x = y = 8;
                    z = 1;
                    return true;
                case BlockSize.ASTC_10x10:
                    x = y = 10;
                    z = 1;
                    return true;
                case BlockSize.ASTC_12x12:
                    x = y = 12;
                    z = 1;
                    return true;
            }
            x = y = z = 0;
            return false;
        }

        internal static GraphicsFormat FitAstcGraphicsFormat(GraphicsFormat graphicsFormat, BlockSize blockSize, TextureType textureType = TextureType.Default)
        {
            switch (graphicsFormat)
            {
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.R8G8B8A8_SRGB:

                case GraphicsFormat.B8G8R8_SRGB:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    if (textureType == TextureType.Normal)
                    {
                        goto case GraphicsFormat.R8G8B8A8_UNorm;
                    }
                    {
                        switch (blockSize)
                        {
                            case BlockSize.ASTC_4x4:
                                return GraphicsFormat.RGBA_ASTC4X4_SRGB;
                            case BlockSize.ASTC_5x5:
                                return GraphicsFormat.RGBA_ASTC5X5_SRGB;
                            case BlockSize.ASTC_6x6:
                                return GraphicsFormat.RGBA_ASTC6X6_SRGB;
                            case BlockSize.ASTC_8x8:
                                return GraphicsFormat.RGBA_ASTC8X8_SRGB;
                            case BlockSize.ASTC_10x10:
                                return GraphicsFormat.RGBA_ASTC10X10_SRGB;
                            case BlockSize.ASTC_12x12:
                                return GraphicsFormat.RGBA_ASTC12X12_SRGB;
                        }
                    }
                    break;
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8A8_UNorm:

                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16G16_UNorm:
                case GraphicsFormat.R16G16B16_UNorm:
                case GraphicsFormat.R16G16B16A16_UNorm:
                    switch (blockSize)
                    {
                        case BlockSize.ASTC_4x4:
                            return GraphicsFormat.RGBA_ASTC4X4_UNorm;
                        case BlockSize.ASTC_5x5:
                            return GraphicsFormat.RGBA_ASTC5X5_UNorm;
                        case BlockSize.ASTC_6x6:
                            return GraphicsFormat.RGBA_ASTC6X6_UNorm;
                        case BlockSize.ASTC_8x8:
                            return GraphicsFormat.RGBA_ASTC8X8_UNorm;
                        case BlockSize.ASTC_10x10:
                            return GraphicsFormat.RGBA_ASTC10X10_UNorm;
                        case BlockSize.ASTC_12x12:
                            return GraphicsFormat.RGBA_ASTC12X12_UNorm;
                    }
                    break;
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R16G16_SFloat:
                case GraphicsFormat.R16G16B16_SFloat:
                case GraphicsFormat.R16G16B16A16_SFloat:

                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.R32G32_SFloat:
                case GraphicsFormat.R32G32B32_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    if (textureType == TextureType.Normal)
                    {
                        goto case GraphicsFormat.R8G8B8A8_UNorm;
                    }
                    switch (blockSize)
                    {
                        case BlockSize.ASTC_4x4:
                            return GraphicsFormat.RGBA_ASTC4X4_UFloat;
                        case BlockSize.ASTC_5x5:
                            return GraphicsFormat.RGBA_ASTC5X5_UFloat;
                        case BlockSize.ASTC_6x6:
                            return GraphicsFormat.RGBA_ASTC6X6_UFloat;
                        case BlockSize.ASTC_8x8:
                            return GraphicsFormat.RGBA_ASTC8X8_UFloat;
                        case BlockSize.ASTC_10x10:
                            return GraphicsFormat.RGBA_ASTC10X10_UFloat;
                        case BlockSize.ASTC_12x12:
                            return GraphicsFormat.RGBA_ASTC12X12_UFloat;
                    }
                    break;
            }
            throw new ArgumentException($"Unsupported ASTC format mapping: GraphicsFormat={graphicsFormat}, BlockSize={blockSize}");
        }

        /// <summary>
        /// Resolves the target ASTC <see cref="GraphicsFormat"/> for a given source format, block size, and texture type.
        /// Normal maps always resolve to linear UNorm ASTC regardless of sRGB or float source format.
        /// </summary>
        public static GraphicsFormat GetTargetAstcFormat(GraphicsFormat sourceFormat, BlockSize blockSize, TextureType textureType = TextureType.Default)
        {
            return AstcGpuBackend.ResolveTargetAstcFormat(sourceFormat, textureType, blockSize, out _, out _);
        }

        internal static bool CanCreateAstcTexture(GraphicsFormat format)
        {
            if (Application.isEditor)
                return true;

#if UNITY_2023_2_OR_NEWER
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
#else
            return SystemInfo.IsFormatSupported(format, FormatUsage.Sample);
#endif
        }

        internal static bool IsFullMipChain(int width, int height, int mipCount)
        {
            if (mipCount <= 1)
                return true;

            int maxDim = Math.Max(width, height);
            int expectedMips = 1;
            while (maxDim > 1)
            {
                maxDim >>= 1;
                expectedMips++;
            }
            return mipCount == expectedMips;
        }

        public static bool IsGpuSupported => AstcGpuBackend.IsSupported;
        public static bool IsCpuSupported => AstcCpuBackend.IsSupported;

        public static bool SupportsGpuDirectCopy(BlockSize blockSize, bool srgb = false, bool isHdr = false)
        {
            return AstcGpuBackend.SupportsDirectCopy(blockSize, srgb, isHdr);
        }

        public static void PreloadGpu(BlockSize blockSize = BlockSize.ASTC_6x6)
        {
            AstcGpuBackend.Preload(blockSize);
        }

        public static void ReleaseGpuCache()
        {
            AstcGpuBackend.ReleaseCache();
        }

        public static bool IsSupport(Texture texture, EncodeBackend backend = EncodeBackend.Auto)
        {
            return backend switch
            {
                EncodeBackend.Cpu => AstcCpuBackend.IsSupported && AstcCpuBackend.IsInputSupported(texture),
                EncodeBackend.Gpu => AstcGpuBackend.IsSupported && AstcGpuBackend.IsInputSupported(texture),
                EncodeBackend.Auto => (AstcGpuBackend.IsSupported && AstcGpuBackend.IsInputSupported(texture))
                                   || (AstcCpuBackend.IsSupported && AstcCpuBackend.IsInputSupported(texture)),
                _ => false,
            };
        }

        private static bool ShouldUseGpu(Texture texture, EncodeBackend backend)
        {
            return backend switch
            {
                EncodeBackend.Gpu => true,
                EncodeBackend.Auto => AstcGpuBackend.IsSupported && AstcGpuBackend.IsInputSupported(texture),
                _ => false,
            };
        }

        private static bool CanFallbackToCpu(Texture texture, EncodeBackend backend)
        {
            return backend == EncodeBackend.Auto
                && AstcCpuBackend.IsSupported
                && AstcCpuBackend.IsInputSupported(texture);
        }

        /// <summary>
        /// Compress the inputTexture and return the compressed data by AstcRaw, you should call Dispose() for the AstcRaw after use.
        /// </summary>
        public static AstcRaw CompressTexture(this Texture2D inputTexture, TextureType textureType, BlockSize blockSize, CompressQuality quality, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                AstcRaw gpuRaw = AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight);
                if (gpuRaw != null || !CanFallbackToCpu(inputTexture, backend))
                    return gpuRaw;
            }

            return AstcCpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: true, useAlphaWeight: useAlphaWeight);
        }

        /// <summary>
        /// Compresses a Texture2D or RenderTexture using an <see cref="AstcCompressOptions"/> configuration struct.
        /// Call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static AstcRaw CompressTexture(this Texture inputTexture, AstcCompressOptions options)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            if (ShouldUseGpu(inputTexture, options.Backend))
            {
                AstcRaw gpuRaw = AstcGpuBackend.CompressTextureSync(inputTexture, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, options.GenerateMipmaps);
                if (gpuRaw != null || !CanFallbackToCpu(inputTexture, options.Backend))
                    return gpuRaw;
            }

            if (inputTexture is Texture2D tex2d)
            {
                return AstcCpuBackend.CompressTextureSync(tex2d, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, options.IncludeMipmaps, options.UseAlphaWeight, options.UsePerceptual, options.AlphaScaleRadius, options.UseUnityJobSystem);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Compresses a RenderTexture on the GPU and returns raw ASTC bytes. Call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static AstcRaw CompressTexture(this RenderTexture inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool includeMipmaps = true, bool useAlphaWeight = false)
        {
            return AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
        }

        /// <summary>
        /// Compresses a Texture2D asynchronously on GPU AsyncGPUReadback or CPU background thread(s) depending on backend.
        /// You should call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static async Task<AstcRaw> CompressTextureAsync(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, int threadCount = 0, bool useAlphaWeight = false, CancellationToken cancellationToken = default, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (ShouldUseGpu(inputTexture, backend))
            {
                AstcRaw gpuRaw = await AstcGpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
                if (gpuRaw != null || !CanFallbackToCpu(inputTexture, backend))
                    return gpuRaw;
            }

            return await AstcCpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: true, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Compresses a Texture2D or RenderTexture asynchronously using an <see cref="AstcCompressOptions"/> configuration struct.
        /// Call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static async Task<AstcRaw> CompressTextureAsync(this Texture inputTexture, AstcCompressOptions options, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            if (ShouldUseGpu(inputTexture, options.Backend))
            {
                AstcRaw gpuRaw = await AstcGpuBackend.CompressTextureAsync(inputTexture, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, cancellationToken, options.GenerateMipmaps);
                if (gpuRaw != null || !CanFallbackToCpu(inputTexture, options.Backend))
                    return gpuRaw;
            }

            if (inputTexture is Texture2D tex2d)
            {
                return await AstcCpuBackend.CompressTextureAsync(tex2d, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, options.IncludeMipmaps, options.UseAlphaWeight, cancellationToken, options.UsePerceptual, options.AlphaScaleRadius);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Compresses a RenderTexture asynchronously on the GPU using AsyncGPUReadback without stalling the main thread.
        /// Call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static Task<AstcRaw> CompressTextureAsync(this RenderTexture inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool includeMipmaps = true, bool useAlphaWeight = false, CancellationToken cancellationToken = default)
        {
            return AstcGpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken);
        }

        /// <summary>
        /// Fills texture pixels with astc raw preformatted data.
        /// </summary>
        /// <param name="inputTexture">Destination Texture2D initialized with matching ASTC format</param>
        /// <param name="raw">Astc raw data</param>
        public static void LoadRawTextureData(this Texture2D inputTexture, AstcRaw raw)
        {
            if (inputTexture == null)
            {
                throw new ArgumentNullException(nameof(inputTexture));
            }

            if (raw != null)
            {
                raw.ThrowIfDisposed();

                inputTexture.LoadRawTextureData(raw.Data, raw.Length);
            }
        }

        /// <summary>
        /// Compress texture in-place at runtime to ASTC format.
        /// </summary>
        public static bool Compress(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool makeNoLongerReadable = true, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                bool ok = AstcGpuBackend.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, useAlphaWeight);
                if (ok || !CanFallbackToCpu(inputTexture, backend))
                    return ok;
            }

            return AstcCpuBackend.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, threadCount, useAlphaWeight);
        }

        /// <summary>
        /// Compresses a Texture2D in-place using an <see cref="AstcCompressOptions"/> configuration struct.
        /// </summary>
        public static bool Compress(this Texture2D inputTexture, AstcCompressOptions options)
        {
            if (ShouldUseGpu(inputTexture, options.Backend))
            {
                bool ok = AstcGpuBackend.CompressInPlace(inputTexture, options.TextureType, options.BlockSize, options.Quality, options.MakeNoLongerReadable, options.UseAlphaWeight, options.GenerateMipmaps);
                if (ok || !CanFallbackToCpu(inputTexture, options.Backend))
                    return ok;
            }

            return AstcCpuBackend.CompressInPlace(inputTexture, options.TextureType, options.BlockSize, options.Quality, options.MakeNoLongerReadable, options.ThreadCount, options.UseAlphaWeight, options.UsePerceptual, options.AlphaScaleRadius, options.UseUnityJobSystem);
        }

        /// <summary>
        /// Compresses any 2D Texture (Texture2D or RenderTexture, without requiring Read/Write on GPU)
        /// into a newly allocated ASTC-compressed Texture2D. Uses zero-readback GPU CopyTexture when supported.
        /// </summary>
        /// <remarks>
        /// Row 0 in the encoded ASTC output corresponds to texel y = 0 (bottom-up), matching Unity's Texture2D,
        /// Graphics.CopyTexture, and standard .astc conventions. RenderTextures rendered by Unity cameras or Blit
        /// already match this layout; RenderTextures populated by custom compute shaders with top-down coordinates
        /// should be flipped vertically before encoding.
        /// </remarks>
        public static Texture2D CompressToTexture(
            this Texture source,
            TextureType textureType = TextureType.Default,
            BlockSize blockSize = BlockSize.ASTC_6x6,
            CompressQuality quality = CompressQuality.Fast,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            EncodeBackend backend = EncodeBackend.Auto)
        {
            if (source == null)
            {
                Debug.LogError("Source texture is null.");
                return null;
            }

            if (ShouldUseGpu(source, backend))
            {
                Texture2D gpuTex = AstcGpuBackend.CompressToTexture(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
                if (gpuTex != null || !CanFallbackToCpu(source, backend))
                    return gpuTex;
            }

            if (source is Texture2D tex2d)
            {
                return AstcCpuBackend.CompressToTexture(tex2d, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Compresses any 2D Texture into a newly allocated ASTC-compressed Texture2D using an <see cref="AstcCompressOptions"/> struct.
        /// </summary>
        public static Texture2D CompressToTexture(this Texture source, AstcCompressOptions options)
        {
            if (source == null)
            {
                Debug.LogError("Source texture is null.");
                return null;
            }

            if (ShouldUseGpu(source, options.Backend))
            {
                Texture2D gpuTex = AstcGpuBackend.CompressToTexture(source, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, options.GenerateMipmaps);
                if (gpuTex != null || !CanFallbackToCpu(source, options.Backend))
                    return gpuTex;
            }

            if (source is Texture2D tex2d)
            {
                return AstcCpuBackend.CompressToTexture(tex2d, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, options.ThreadCount, options.UsePerceptual, options.AlphaScaleRadius, options.UseUnityJobSystem);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Asynchronously compresses any 2D Texture (Texture2D or RenderTexture) into a newly allocated ASTC-compressed Texture2D
        /// without blocking the main thread (uses zero-readback CopyTexture when aligned, AsyncGPUReadback on GPU, or background threads on CPU).
        /// Must be called from the Unity main thread.
        /// </summary>
        public static async Task<Texture2D> CompressToTextureAsync(
            this Texture source,
            TextureType textureType = TextureType.Default,
            BlockSize blockSize = BlockSize.ASTC_6x6,
            CompressQuality quality = CompressQuality.Fast,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            int threadCount = 0,
            CancellationToken cancellationToken = default,
            EncodeBackend backend = EncodeBackend.Auto)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (source == null)
            {
                Debug.LogError("Source texture is null.");
                return null;
            }

            if (ShouldUseGpu(source, backend))
            {
                Texture2D gpuTex = await AstcGpuBackend.CompressToTextureAsync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken);
                if (gpuTex != null || !CanFallbackToCpu(source, backend))
                    return gpuTex;
            }

            if (source is Texture2D tex2d)
            {
                return await AstcCpuBackend.CompressToTextureAsync(tex2d, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, threadCount, cancellationToken: cancellationToken);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Asynchronously compresses any 2D Texture into a newly allocated ASTC-compressed Texture2D using an <see cref="AstcCompressOptions"/> struct.
        /// </summary>
        public static async Task<Texture2D> CompressToTextureAsync(this Texture source, AstcCompressOptions options, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (source == null)
            {
                Debug.LogError("Source texture is null.");
                return null;
            }

            if (ShouldUseGpu(source, options.Backend))
            {
                Texture2D gpuTex = await AstcGpuBackend.CompressToTextureAsync(source, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, cancellationToken, options.GenerateMipmaps);
                if (gpuTex != null || !CanFallbackToCpu(source, options.Backend))
                    return gpuTex;
            }

            if (source is Texture2D tex2d)
            {
                return await AstcCpuBackend.CompressToTextureAsync(tex2d, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight, options.ThreadCount, options.UsePerceptual, options.AlphaScaleRadius, cancellationToken);
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Records a GPU ASTC compression dispatch and zero-readback CopyTexture into a CommandBuffer.
        /// Ideal for batching, Async Compute queues, procedural textures, and virtual texture atlas tile updates.
        /// </summary>
        /// <param name="destX">Destination X offset in texels (must be a non-negative multiple of the ASTC block width).</param>
        /// <param name="destY">Destination Y offset in texels (must be a non-negative multiple of the ASTC block height).</param>
        /// <remarks>
        /// Row 0 is encoded at texel y = 0 without vertical flipping, matching Unity Texture2D and CopyTexture layout.
        /// If <paramref name="source"/> is a RenderTexture written by a custom compute shader using top-down y,
        /// flip it prior to encoding.
        /// </remarks>
        public static void Encode(
            CommandBuffer cmd,
            Texture source,
            Texture destination,
            TextureType textureType = TextureType.Default,
            BlockSize blockSize = BlockSize.ASTC_6x6,
            CompressQuality quality = CompressQuality.Fast,
            int sourceMip = 0,
            int destMip = 0,
            int destX = 0,
            int destY = 0,
            bool useAlphaWeight = false,
            int sourceSlice = 0,
            int destSlice = 0)
        {
            AstcGpuBackend.Encode(cmd, source, destination, textureType, blockSize, quality, sourceMip, destMip, destX, destY, useAlphaWeight, sourceSlice, destSlice);
        }

        /// <summary>
        /// Compresses all 6 faces (and optional mipmaps) of a <see cref="Cubemap"/> into a new ASTC-compressed <see cref="Cubemap"/> on the GPU with zero CPU readback.
        /// </summary>
        public static Cubemap CompressCubemap(this Cubemap source, AstcCompressOptions options)
        {
            if (source == null)
            {
                Debug.LogError("Source Cubemap is null.");
                return null;
            }
            return AstcGpuBackend.CompressCubemap(source, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight);
        }

        /// <summary>
        /// Compresses all slices (and optional mipmaps) of a <see cref="Texture2DArray"/> into a new ASTC-compressed <see cref="Texture2DArray"/> on the GPU with zero CPU readback.
        /// </summary>
        public static Texture2DArray CompressTexture2DArray(this Texture2DArray source, AstcCompressOptions options)
        {
            if (source == null)
            {
                Debug.LogError("Source Texture2DArray is null.");
                return null;
            }
            return AstcGpuBackend.CompressTexture2DArray(source, options.TextureType, options.BlockSize, options.Quality, options.IncludeMipmaps, options.UseAlphaWeight);
        }

        /// <summary>
        /// Compresses raw pixel bytes directly on the CPU without creating a Unity <see cref="Texture2D"/>.
        /// Ideal for webcam feeds, procedural generators, and background-thread pipelines.
        /// Call Dispose() on the returned <see cref="AstcRaw"/> after use.
        /// </summary>
        public static unsafe AstcRaw CompressRawPixels(
            Unity.Collections.NativeArray<byte> rawPixels,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            AstcCompressOptions options)
        {
            void* ptr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rawPixels);
            return AstcCpuBackend.CompressRawPixels(
                (IntPtr)ptr,
                rawPixels.Length,
                width,
                height,
                sourceFormat,
                options.TextureType,
                options.BlockSize,
                options.Quality,
                options.ThreadCount,
                options.UseAlphaWeight,
                options.UsePerceptual,
                options.AlphaScaleRadius,
                options.UseUnityJobSystem);
        }

        /// <summary>
        /// Compresses raw pixel bytes directly into a caller-owned <see cref="Unity.Collections.NativeArray{T}"/>
        /// with zero unmanaged heap allocations for 4-channel formats (RGBA8/BGRA8/RGBA16F/RGBA32F).
        /// Ideal for high-frequency webcam/streaming loops reusing a persistent destination buffer.
        /// </summary>
        public static unsafe bool CompressRawPixelsInto(
            Unity.Collections.NativeArray<byte> rawPixels,
            Unity.Collections.NativeArray<byte> destination,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            AstcCompressOptions options,
            out int bytesWritten)
        {
            void* srcPtr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rawPixels);
            void* dstPtr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(destination);
            return AstcCpuBackend.CompressRawPixelsInto(
                (IntPtr)srcPtr,
                rawPixels.Length,
                (IntPtr)dstPtr,
                destination.Length,
                width,
                height,
                sourceFormat,
                options.TextureType,
                options.BlockSize,
                options.Quality,
                options.ThreadCount,
                options.UseAlphaWeight,
                options.UsePerceptual,
                options.AlphaScaleRadius,
                options.UseUnityJobSystem,
                out bytesWritten);
        }

        /// <summary>
        /// Compresses a managed raw pixel byte array directly on the CPU without creating a Unity <see cref="Texture2D"/>.
        /// Call Dispose() on the returned <see cref="AstcRaw"/> after use.
        /// </summary>
        public static unsafe AstcRaw CompressRawPixels(
            byte[] rawPixels,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            AstcCompressOptions options)
        {
            if (rawPixels == null || rawPixels.Length == 0)
            {
                Debug.LogError("Raw pixel array is null or empty.");
                return null;
            }

            fixed (byte* ptr = rawPixels)
            {
                return AstcCpuBackend.CompressRawPixels(
                    (IntPtr)ptr,
                    rawPixels.Length,
                    width,
                    height,
                    sourceFormat,
                    options.TextureType,
                    options.BlockSize,
                    options.Quality,
                    options.ThreadCount,
                    options.UseAlphaWeight,
                    options.UsePerceptual,
                    options.AlphaScaleRadius,
                    options.UseUnityJobSystem);
            }
        }

        /// <summary>
        /// Asynchronously compresses raw pixel bytes on a background thread without creating a Unity <see cref="Texture2D"/>.
        /// The input buffer is snapshotted before returning, so the caller may safely reuse or dispose it immediately.
        /// </summary>
        public static unsafe Task<AstcRaw> CompressRawPixelsAsync(
            Unity.Collections.NativeArray<byte> rawPixels,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            AstcCompressOptions options,
            CancellationToken cancellationToken = default)
        {
            void* ptr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rawPixels);
            return AstcCpuBackend.CompressRawPixelsAsync(
                (IntPtr)ptr,
                rawPixels.Length,
                width,
                height,
                sourceFormat,
                options.TextureType,
                options.BlockSize,
                options.Quality,
                options.ThreadCount,
                options.UseAlphaWeight,
                options.UsePerceptual,
                options.AlphaScaleRadius,
                cancellationToken);
        }

        /// <summary>
        /// Asynchronously compresses a managed raw pixel byte array on a background thread without creating a Unity <see cref="Texture2D"/>.
        /// </summary>
        public static unsafe Task<AstcRaw> CompressRawPixelsAsync(
            byte[] rawPixels,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            AstcCompressOptions options,
            CancellationToken cancellationToken = default)
        {
            if (rawPixels == null || rawPixels.Length == 0)
            {
                Debug.LogError("Raw pixel array is null or empty.");
                return Task.FromResult<AstcRaw>(null);
            }

            fixed (byte* ptr = rawPixels)
            {
                return AstcCpuBackend.CompressRawPixelsAsync(
                    (IntPtr)ptr,
                    rawPixels.Length,
                    width,
                    height,
                    sourceFormat,
                    options.TextureType,
                    options.BlockSize,
                    options.Quality,
                    options.ThreadCount,
                    options.UseAlphaWeight,
                    options.UsePerceptual,
                    options.AlphaScaleRadius,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Encode any 2D Texture (Texture2D or RenderTexture) to ASTC file bytes with a 16-byte .astc header (mip 0 only).
        /// </summary>
        public static byte[] EncodeToASTC(this Texture inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Medium, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            // Standard .astc file header describes a single image level (mip 0 only)
            AstcRaw raw = null;
            if (ShouldUseGpu(inputTexture, backend))
            {
                raw = AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: false, useAlphaWeight: useAlphaWeight);
                if (raw == null && CanFallbackToCpu(inputTexture, backend))
                {
                    raw = AstcCpuBackend.CompressTextureSync((Texture2D)inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: false, useAlphaWeight: useAlphaWeight);
                }
            }
            else if (inputTexture is Texture2D tex2d)
            {
                raw = AstcCpuBackend.CompressTextureSync(tex2d, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: false, useAlphaWeight: useAlphaWeight);
            }
            else
            {
                Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
                return null;
            }

            using (raw)
            {
                return BuildAstcFileBytes(raw, blockSize, inputTexture.width, inputTexture.height);
            }
        }

        /// <summary>
        /// Encodes any 2D Texture to .astc file bytes (mip 0 only) using an <see cref="AstcCompressOptions"/> struct.
        /// </summary>
        public static byte[] EncodeToASTC(this Texture inputTexture, AstcCompressOptions options)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            AstcRaw raw = null;
            if (ShouldUseGpu(inputTexture, options.Backend))
            {
                raw = AstcGpuBackend.CompressTextureSync(inputTexture, options.TextureType, options.BlockSize, options.Quality, includeMipmaps: false, useAlphaWeight: options.UseAlphaWeight);
                if (raw == null && CanFallbackToCpu(inputTexture, options.Backend))
                {
                    raw = AstcCpuBackend.CompressTextureSync((Texture2D)inputTexture, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, includeMipmaps: false, options.UseAlphaWeight, options.UsePerceptual, options.AlphaScaleRadius);
                }
            }
            else if (inputTexture is Texture2D tex2d)
            {
                raw = AstcCpuBackend.CompressTextureSync(tex2d, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, includeMipmaps: false, options.UseAlphaWeight, options.UsePerceptual, options.AlphaScaleRadius);
            }
            else
            {
                Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
                return null;
            }

            using (raw)
            {
                return BuildAstcFileBytes(raw, options.BlockSize, inputTexture.width, inputTexture.height);
            }
        }

        /// <summary>
        /// Asynchronously encodes any 2D Texture (Texture2D or RenderTexture) to .astc file bytes (mip 0 only).
        /// Uses AsyncGPUReadback on GPU backend or background thread(s) on CPU backend.
        /// </summary>
        public static async Task<byte[]> EncodeToASTCAsync(
            this Texture inputTexture,
            TextureType textureType = TextureType.Default,
            BlockSize blockSize = BlockSize.ASTC_6x6,
            CompressQuality quality = CompressQuality.Medium,
            int threadCount = 0,
            bool useAlphaWeight = false,
            CancellationToken cancellationToken = default,
            EncodeBackend backend = EncodeBackend.Auto)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            int width = inputTexture.width;
            int height = inputTexture.height;

            AstcRaw raw;
            if (ShouldUseGpu(inputTexture, backend))
            {
                raw = await AstcGpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, includeMipmaps: false, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
                if (raw == null && CanFallbackToCpu(inputTexture, backend))
                {
                    raw = await AstcCpuBackend.CompressTextureAsync((Texture2D)inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: false, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
                }
            }
            else if (inputTexture is Texture2D tex2d)
            {
                raw = await AstcCpuBackend.CompressTextureAsync(tex2d, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: false, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
            }
            else
            {
                Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
                return null;
            }

            using (raw)
            {
                return BuildAstcFileBytes(raw, blockSize, width, height);
            }
        }

        /// <summary>
        /// Asynchronously encodes any 2D Texture to .astc file bytes (mip 0 only) using an <see cref="AstcCompressOptions"/> struct.
        /// </summary>
        public static async Task<byte[]> EncodeToASTCAsync(this Texture inputTexture, AstcCompressOptions options, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            int width = inputTexture.width;
            int height = inputTexture.height;

            AstcRaw raw;
            if (ShouldUseGpu(inputTexture, options.Backend))
            {
                raw = await AstcGpuBackend.CompressTextureAsync(inputTexture, options.TextureType, options.BlockSize, options.Quality, includeMipmaps: false, useAlphaWeight: options.UseAlphaWeight, cancellationToken: cancellationToken);
                if (raw == null && CanFallbackToCpu(inputTexture, options.Backend))
                {
                    raw = await AstcCpuBackend.CompressTextureAsync((Texture2D)inputTexture, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, includeMipmaps: false, options.UseAlphaWeight, cancellationToken, options.UsePerceptual, options.AlphaScaleRadius);
                }
            }
            else if (inputTexture is Texture2D tex2d)
            {
                raw = await AstcCpuBackend.CompressTextureAsync(tex2d, options.TextureType, options.BlockSize, options.Quality, options.ThreadCount, includeMipmaps: false, options.UseAlphaWeight, cancellationToken, options.UsePerceptual, options.AlphaScaleRadius);
            }
            else
            {
                Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
                return null;
            }

            using (raw)
            {
                return BuildAstcFileBytes(raw, options.BlockSize, width, height);
            }
        }

        /// <summary>
        /// Software-decompresses raw ASTC block data (mip 0) into 4-channel RGBA bytes using the native CPU astc-encoder library.
        /// Set <paramref name="outputBitness"/> to 8 (RGBA8), 16 (RGBA16F), or 32 (RGBA32F).
        /// </summary>
        public static byte[] Decompress(
            AstcRaw raw,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb = false,
            bool isHdr = false,
            int outputBitness = 8,
            int threadCount = 1)
        {
            if (raw == null)
                return null;

            raw.ThrowIfDisposed();
            return AstcCpuBackend.DecompressToBytes(raw.Data, raw.Length, width, height, blockSize, srgb, isHdr, outputBitness, threadCount);
        }

        /// <summary>
        /// Software-decompresses raw ASTC block bytes (mip 0) into 4-channel RGBA bytes using the native CPU astc-encoder library.
        /// Set <paramref name="outputBitness"/> to 8 (RGBA8), 16 (RGBA16F), or 32 (RGBA32F).
        /// </summary>
        public static byte[] Decompress(
            byte[] compressedBytes,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb = false,
            bool isHdr = false,
            int outputBitness = 8,
            int threadCount = 1)
        {
            if (compressedBytes == null)
                return null;

            return AstcCpuBackend.DecompressToBytes(compressedBytes, 0, compressedBytes.Length, width, height, blockSize, srgb, isHdr, outputBitness, threadCount);
        }

        /// <summary>
        /// Software-decompresses raw ASTC block data (mip 0) into an uncompressed RGBA <see cref="Texture2D"/> (RGBA8 for LDR, RGBA16F for HDR).
        /// Useful as a fallback on platforms without hardware ASTC support or for verification.
        /// </summary>
        public static Texture2D DecompressToTexture(
            AstcRaw raw,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb = false,
            bool isHdr = false,
            int threadCount = 1,
            bool makeNoLongerReadable = false)
        {
            if (raw == null)
                return null;

            raw.ThrowIfDisposed();
            return AstcCpuBackend.DecompressToTexture(raw.Data, raw.Length, width, height, blockSize, srgb, isHdr, threadCount, makeNoLongerReadable);
        }

        /// <summary>
        /// Parses a standard .astc file (16-byte header + ASTC blocks) and software-decompresses it into an uncompressed RGBA <see cref="Texture2D"/>.
        /// </summary>
        public static Texture2D DecompressAstcFile(
            byte[] astcFileBytes,
            bool srgb = false,
            bool isHdr = false,
            int threadCount = 1,
            bool makeNoLongerReadable = false)
        {
            return AstcCpuBackend.DecompressAstcFileToTexture(astcFileBytes, srgb, isHdr, threadCount, makeNoLongerReadable);
        }

        /// <summary>
        /// Encodes any 2D Texture (including optional mipmaps) into a standard Khronos KTX v1 container byte array.
        /// Unlike <see cref="EncodeToASTC(Texture, AstcCompressOptions)"/>, KTX v1 preserves full mipmap chains and exact sRGB/HDR metadata.
        /// </summary>
        public static byte[] EncodeToKTX(this Texture inputTexture, AstcCompressOptions options)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            int width = inputTexture.width;
            int height = inputTexture.height;
            GraphicsFormat targetFormat = GetTargetAstcFormat(inputTexture.graphicsFormat, options.BlockSize, options.TextureType);

            using AstcRaw raw = inputTexture.CompressTexture(options);
            if (raw == null)
                return null;

            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, width, height, options.BlockSize);
            if (mipCount <= 0)
                return null;

            return AstcKtxContainer.Encode(raw, width, height, mipCount, targetFormat);
        }

        /// <summary>
        /// Asynchronously encodes any 2D Texture (including optional mipmaps) into a standard Khronos KTX v1 container byte array.
        /// </summary>
        public static async Task<byte[]> EncodeToKTXAsync(this Texture inputTexture, AstcCompressOptions options, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            int width = inputTexture.width;
            int height = inputTexture.height;
            GraphicsFormat targetFormat = GetTargetAstcFormat(inputTexture.graphicsFormat, options.BlockSize, options.TextureType);

            using AstcRaw raw = await inputTexture.CompressTextureAsync(options, cancellationToken);
            if (raw == null)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, width, height, options.BlockSize);
            if (mipCount <= 0)
                return null;

            return AstcKtxContainer.Encode(raw, width, height, mipCount, targetFormat);
        }

        /// <summary>
        /// Loads a standard Khronos KTX v1 ASTC container into a GPU-ready <see cref="Texture2D"/> (with full mipmap chain).
        /// Automatically falls back to CPU software decompression on devices without hardware ASTC support.
        /// </summary>
        public static Texture2D LoadFromKTX(byte[] ktxBytes, bool makeNoLongerReadable = true, string textureName = null)
        {
            return AstcKtxContainer.LoadTexture(ktxBytes, makeNoLongerReadable, textureName);
        }

        private static byte[] BuildAstcFileBytes(AstcRaw raw, BlockSize blockSize, int width, int height)
        {
            if (raw == null)
            {
                return null;
            }

            raw.ThrowIfDisposed();

            byte[] output = new byte[AstcHeader.HeaderSize + raw.Length];
            GetBlockSize(blockSize, out uint dimX, out uint dimY, out uint dimZ);
            AstcHeader.WriteTo(output, 0, dimX, dimY, dimZ, width, height, 1);
            Marshal.Copy(raw.Data, output, AstcHeader.HeaderSize, raw.Length);
            return output;
        }
    }
}
