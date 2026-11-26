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

        public static bool IsGpuSupported => AstcGpuBackend.IsSupported;

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

        /// <summary>
        /// Compress the inputTexture and return the compressed data by AstcRaw, you should call Dispose() for the AstcRaw after use.
        /// </summary>
        /// <param name="inputTexture">Input texture (CPU backend requires uncompressed readable Texture2D; GPU backend supports non-readable Texture2D)</param>
        /// <param name="textureType">Indicate that compress for color texture or normal texture</param>
        /// <param name="blockSize">Astc compress ratio</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="threadCount">Worker thread count for CPU backend (1 = single-threaded, &lt;= 0 = auto)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures; leave false for channel-packed maps)</param>
        /// <param name="backend">Execution backend (Auto prefers GPU with CPU fallback)</param>
        /// <returns>CompressedData</returns>
        public static AstcRaw CompressTexture(this Texture2D inputTexture, TextureType textureType, BlockSize blockSize, CompressQuality quality, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight);
            }

            return AstcCpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: true, useAlphaWeight: useAlphaWeight);
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
        /// <param name="inputTexture">Input texture (CPU backend requires readable Texture2D; GPU backend supports non-readable Texture2D)</param>
        /// <param name="textureType">Indicate that compress for color texture or normal texture</param>
        /// <param name="blockSize">Astc compress ratio</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="threadCount">Worker thread count for CPU backend (&lt;= 0 = auto based on CPU cores)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures; leave false for channel-packed maps)</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="backend">Execution backend (Auto prefers GPU with CPU fallback)</param>
        /// <returns>Task yielding compressed AstcRaw data or null on failure</returns>
        public static Task<AstcRaw> CompressTextureAsync(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, int threadCount = 0, bool useAlphaWeight = false, CancellationToken cancellationToken = default, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AstcRaw>(cancellationToken);
            }

            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
            }

            return AstcCpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: true, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
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
        /// <param name="inputTexture">Input Texture2D (must have Read/Write enabled for in-place Reinitialize; use CompressToTexture for non-readable textures)</param>
        /// <param name="textureType">Indicate that compress for color texture or normal texture</param>
        /// <param name="blockSize">Astc compress ratio</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="makeNoLongerReadable">When set to true, Unity discards the copy of pixel data in CPU-addressable memory after this operation.</param>
        /// <param name="threadCount">Worker thread count for CPU backend (1 = single-threaded, &lt;= 0 = auto)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures; leave false for channel-packed maps)</param>
        /// <param name="backend">Execution backend (Auto prefers GPU with CPU fallback)</param>
        /// <returns>Success or not</returns>
        public static bool Compress(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool makeNoLongerReadable = true, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, useAlphaWeight);
            }

            return AstcCpuBackend.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, threadCount, useAlphaWeight);
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
                return AstcGpuBackend.CompressToTexture(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
            }

            if (source is Texture2D tex2d)
            {
                return AstcCpuBackend.CompressToTexture(tex2d, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
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
            bool useAlphaWeight = false)
        {
            AstcGpuBackend.Encode(cmd, source, destination, textureType, blockSize, quality, sourceMip, destMip, destX, destY, useAlphaWeight);
        }

        /// <summary>
        /// Encode texture2D to ASTC file bytes with a 16-byte .astc header (mip 0 only).
        /// </summary>
        /// <param name="inputTexture">Input texture</param>
        /// <param name="textureType">Color or normal texture</param>
        /// <param name="blockSize">ASTC block size</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="threadCount">Worker thread count for CPU backend (1 = single-threaded, &lt;= 0 = auto)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures)</param>
        /// <param name="backend">Execution backend (Auto prefers GPU with CPU fallback)</param>
        /// <returns>.astc file bytes or null on failure</returns>
        public static byte[] EncodeToASTC(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Medium, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Auto)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            // Standard .astc file header describes a single image level (mip 0 only)
            using AstcRaw raw = ShouldUseGpu(inputTexture, backend)
                ? AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: false, useAlphaWeight: useAlphaWeight)
                : AstcCpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, threadCount: threadCount, includeMipmaps: false, useAlphaWeight: useAlphaWeight);

            return BuildAstcFileBytes(raw, blockSize, inputTexture.width, inputTexture.height);
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
