using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Aperture.Astcenc.Runtime
{
    internal static class AstcGpuBackend
    {
        private const string k_ShaderResourcePath = "AstcGpuEncoder";
        private const int k_BytesPerBlock = 16;

        private const uint k_ModeLdrRgb = 0u;
        private const uint k_ModeLdrRgba = 1u;
        private const uint k_ModeLdrNormal = 2u;
        private const uint k_ModeHdrRgb = 3u;
        private const uint k_ModeHdrRgba = 4u;

        private static readonly int s_PropSourceTex = Shader.PropertyToID("_AstcSourceTex");
        private static readonly int s_PropDestTex = Shader.PropertyToID("_AstcDestTex");
        private static readonly int s_PropDestBuffer = Shader.PropertyToID("_AstcDestBuffer");
        private static readonly int s_PropSrcWidth = Shader.PropertyToID("_AstcSrcWidth");
        private static readonly int s_PropSrcHeight = Shader.PropertyToID("_AstcSrcHeight");
        private static readonly int s_PropMipLevel = Shader.PropertyToID("_AstcMipLevel");
        private static readonly int s_PropBlockCountX = Shader.PropertyToID("_AstcBlockCountX");
        private static readonly int s_PropBlockCountY = Shader.PropertyToID("_AstcBlockCountY");
        private static readonly int s_PropBufferBlockOffset = Shader.PropertyToID("_AstcBufferBlockOffset");
        private static readonly int s_PropMode = Shader.PropertyToID("_AstcMode");
        private static readonly int s_PropIsSRGB = Shader.PropertyToID("_AstcIsSRGB");
        private static readonly int s_PropUseAlphaWeight = Shader.PropertyToID("_AstcUseAlphaWeight");
        private static readonly int s_PropQuality = Shader.PropertyToID("_AstcQuality");

        private static ComputeShader s_Shader;
        private static bool s_ShaderLoadAttempted;
        private static int[] s_TexKernels;
        private static int[] s_BufKernels;

        private static readonly int s_PropTempBlocksRT = Shader.PropertyToID("_AstcTempBlocksRT");

        private static bool IsLoadStoreSupported(GraphicsFormat format)
        {
#if UNITY_2023_2_OR_NEWER
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore);
#else
            return SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore);
#endif
        }

        internal static bool IsSampleSupported(GraphicsFormat format)
        {
#if UNITY_2023_2_OR_NEWER
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
#else
            return SystemInfo.IsFormatSupported(format, FormatUsage.Sample);
#endif
        }

        internal static bool IsSupported
        {
            get
            {
                if (!SystemInfo.supportsComputeShaders)
                    return false;

                if (!IsLoadStoreSupported(GraphicsFormat.R32G32B32A32_UInt))
                    return false;

                return EnsureShaderLoaded();
            }
        }

        internal static bool IsInputSupported(Texture texture)
        {
            if (texture == null
                || texture.dimension != TextureDimension.Tex2D
                || texture.width <= 0
                || texture.height <= 0)
            {
                return false;
            }

            if (texture is RenderTexture rt && rt.antiAliasing > 1)
                return false;

            GraphicsFormat format = texture.graphicsFormat;
            if (format == GraphicsFormat.None)
                return false;

            // _AstcSourceTex is bound as Texture2D<float4>; depth/stencil/integer formats cannot be loaded as float4.
            if (GraphicsFormatUtility.IsDepthFormat(format)
                || GraphicsFormatUtility.IsStencilFormat(format)
                || GraphicsFormatUtility.IsIntegerFormat(format))
            {
                return false;
            }

            return true;
        }

        internal static bool SupportsDirectCopy(BlockSize blockSize, bool srgb, bool isHdr)
        {
            if (!IsSupported)
                return false;

            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
                return false;

            GraphicsFormat astcFormat = GetAstcGraphicsFormat(blockSize, srgb, isHdr);
            return IsSampleSupported(astcFormat);
        }

        internal static bool CanUseDirectCopy(int width, int height, int mipCount, BlockSize blockSize, GraphicsFormat astcFormat)
        {
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
                return false;

            if (!IsSampleSupported(astcFormat))
                return false;

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return false;

            for (int mip = 0; mip < mipCount; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                if ((mipW % (int)bx) != 0 || (mipH % (int)by) != 0)
                    return false;
            }

            return true;
        }

        internal static GraphicsFormat ResolveTargetAstcFormat(GraphicsFormat sourceFormat, TextureType textureType, BlockSize blockSize, out bool isSRGB, out bool isHdr)
        {
            bool sourceIsSRGB = GraphicsFormatUtility.IsSRGBFormat(sourceFormat);
            isHdr = !sourceIsSRGB && GraphicsFormatUtility.IsFloatFormat(sourceFormat);
            // Normal maps store linear tangent-space vectors; never emit an sRGB ASTC target for them.
            isSRGB = sourceIsSRGB && textureType != TextureType.Normal;
            return GetAstcGraphicsFormat(blockSize, isSRGB, isHdr);
        }

        internal static GraphicsFormat GetAstcGraphicsFormat(BlockSize blockSize, bool srgb, bool isHdr)
        {
            if (isHdr)
            {
                return blockSize switch
                {
                    BlockSize.ASTC_4x4 => GraphicsFormat.RGBA_ASTC4X4_UFloat,
                    BlockSize.ASTC_5x5 => GraphicsFormat.RGBA_ASTC5X5_UFloat,
                    BlockSize.ASTC_6x6 => GraphicsFormat.RGBA_ASTC6X6_UFloat,
                    BlockSize.ASTC_8x8 => GraphicsFormat.RGBA_ASTC8X8_UFloat,
                    BlockSize.ASTC_10x10 => GraphicsFormat.RGBA_ASTC10X10_UFloat,
                    BlockSize.ASTC_12x12 => GraphicsFormat.RGBA_ASTC12X12_UFloat,
                    _ => throw new ArgumentException($"Unsupported ASTC block size: {blockSize}", nameof(blockSize)),
                };
            }

            if (srgb)
            {
                return blockSize switch
                {
                    BlockSize.ASTC_4x4 => GraphicsFormat.RGBA_ASTC4X4_SRGB,
                    BlockSize.ASTC_5x5 => GraphicsFormat.RGBA_ASTC5X5_SRGB,
                    BlockSize.ASTC_6x6 => GraphicsFormat.RGBA_ASTC6X6_SRGB,
                    BlockSize.ASTC_8x8 => GraphicsFormat.RGBA_ASTC8X8_SRGB,
                    BlockSize.ASTC_10x10 => GraphicsFormat.RGBA_ASTC10X10_SRGB,
                    BlockSize.ASTC_12x12 => GraphicsFormat.RGBA_ASTC12X12_SRGB,
                    _ => throw new ArgumentException($"Unsupported ASTC block size: {blockSize}", nameof(blockSize)),
                };
            }

            return blockSize switch
            {
                BlockSize.ASTC_4x4 => GraphicsFormat.RGBA_ASTC4X4_UNorm,
                BlockSize.ASTC_5x5 => GraphicsFormat.RGBA_ASTC5X5_UNorm,
                BlockSize.ASTC_6x6 => GraphicsFormat.RGBA_ASTC6X6_UNorm,
                BlockSize.ASTC_8x8 => GraphicsFormat.RGBA_ASTC8X8_UNorm,
                BlockSize.ASTC_10x10 => GraphicsFormat.RGBA_ASTC10X10_UNorm,
                BlockSize.ASTC_12x12 => GraphicsFormat.RGBA_ASTC12X12_UNorm,
                _ => throw new ArgumentException($"Unsupported ASTC block size: {blockSize}", nameof(blockSize)),
            };
        }

        private static int BlockSizeToSlot(BlockSize blockSize)
        {
            return blockSize switch
            {
                BlockSize.ASTC_4x4 => 0,
                BlockSize.ASTC_5x5 => 1,
                BlockSize.ASTC_6x6 => 2,
                BlockSize.ASTC_8x8 => 3,
                BlockSize.ASTC_10x10 => 4,
                BlockSize.ASTC_12x12 => 5,
                _ => -1,
            };
        }

        private static uint MapQualityTier(CompressQuality quality)
        {
            return quality switch
            {
                CompressQuality.Fastest => 0u,
                CompressQuality.Fast => 0u,
                CompressQuality.Medium => 1u,
                CompressQuality.Thorough => 2u,
                CompressQuality.VeryThorough => 2u,
                CompressQuality.Exhaustive => 2u,
                _ => 0u,
            };
        }

        private static uint ResolveShaderMode(GraphicsFormat sourceFormat, TextureType textureType, out bool isSRGB, out bool isHdr)
        {
            isSRGB = GraphicsFormatUtility.IsSRGBFormat(sourceFormat);
            isHdr = !isSRGB && GraphicsFormatUtility.IsFloatFormat(sourceFormat);

            if (textureType == TextureType.Normal && !isHdr)
            {
                return k_ModeLdrNormal;
            }

            bool hasAlpha = GraphicsFormatUtility.HasAlphaChannel(sourceFormat);
            if (isHdr)
            {
                return hasAlpha ? k_ModeHdrRgba : k_ModeHdrRgb;
            }

            return hasAlpha ? k_ModeLdrRgba : k_ModeLdrRgb;
        }

        private static bool EnsureShaderLoaded()
        {
            if (s_Shader != null)
                return true;

            if (s_ShaderLoadAttempted)
                return false;

            s_ShaderLoadAttempted = true;
            s_Shader = Resources.Load<ComputeShader>(k_ShaderResourcePath);
            if (s_Shader == null)
            {
                Debug.LogError($"Failed to load ComputeShader from Resources/{k_ShaderResourcePath}.compute.");
                return false;
            }

            s_TexKernels = new int[6]
            {
                s_Shader.FindKernel("EncodeTex4x4"),
                s_Shader.FindKernel("EncodeTex5x5"),
                s_Shader.FindKernel("EncodeTex6x6"),
                s_Shader.FindKernel("EncodeTex8x8"),
                s_Shader.FindKernel("EncodeTex10x10"),
                s_Shader.FindKernel("EncodeTex12x12"),
            };

            s_BufKernels = new int[6]
            {
                s_Shader.FindKernel("EncodeBuf4x4"),
                s_Shader.FindKernel("EncodeBuf5x5"),
                s_Shader.FindKernel("EncodeBuf6x6"),
                s_Shader.FindKernel("EncodeBuf8x8"),
                s_Shader.FindKernel("EncodeBuf10x10"),
                s_Shader.FindKernel("EncodeBuf12x12"),
            };

            return true;
        }

        private static RenderTextureDescriptor CreateBlockRTDescriptor(int blockW, int blockH)
        {
            return new RenderTextureDescriptor(blockW, blockH, GraphicsFormat.R32G32B32A32_UInt, 0)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
        }

        internal static void Preload(BlockSize blockSize)
        {
            if (!IsSupported)
                return;

            int slot = BlockSizeToSlot(blockSize);
            if (slot < 0)
                return;

            var dummySrc = new Texture2D(4, 4, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(CreateBlockRTDescriptor(1, 1));
                rt.Create();
                int texKernel = s_TexKernels[slot];
                s_Shader.SetTexture(texKernel, s_PropSourceTex, dummySrc);
                s_Shader.SetTexture(texKernel, s_PropDestTex, rt);
                SetCommonUniforms(s_Shader, 4, 4, 0, 1, 1, 0, k_ModeLdrRgb, false, false, 0u);
                s_Shader.Dispatch(texKernel, 1, 1, 1);
            }
            finally
            {
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(dummySrc);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ReleaseCache();
            Application.quitting -= ReleaseCache;
            Application.quitting += ReleaseCache;
        }

        /// <summary>
        /// Drops the cached ComputeShader / kernel references so they are reloaded on next use.
        /// Temporary block render targets are pooled per call (RenderTexture.GetTemporary / CommandBuffer.GetTemporaryRT),
        /// so there is no long-lived GPU memory held by this backend.
        /// </summary>
        internal static void ReleaseCache()
        {
            s_Shader = null;
            s_ShaderLoadAttempted = false;
            s_TexKernels = null;
            s_BufKernels = null;
        }

        private static void SetCommonUniforms(
            ComputeShader shader,
            int srcWidth, int srcHeight, int mipLevel,
            int blockCountX, int blockCountY, int bufferBlockOffset,
            uint mode, bool isSRGB, bool useAlphaWeight, uint qualityTier)
        {
            shader.SetInt(s_PropSrcWidth, srcWidth);
            shader.SetInt(s_PropSrcHeight, srcHeight);
            shader.SetInt(s_PropMipLevel, mipLevel);
            shader.SetInt(s_PropBlockCountX, blockCountX);
            shader.SetInt(s_PropBlockCountY, blockCountY);
            shader.SetInt(s_PropBufferBlockOffset, bufferBlockOffset);
            shader.SetInt(s_PropMode, (int)mode);
            shader.SetInt(s_PropIsSRGB, isSRGB ? 1 : 0);
            shader.SetInt(s_PropUseAlphaWeight, useAlphaWeight ? 1 : 0);
            shader.SetInt(s_PropQuality, (int)qualityTier);
        }

        private static void SetCommonUniforms(
            CommandBuffer cmd,
            ComputeShader shader,
            int srcWidth, int srcHeight, int mipLevel,
            int blockCountX, int blockCountY, int bufferBlockOffset,
            uint mode, bool isSRGB, bool useAlphaWeight, uint qualityTier)
        {
            cmd.SetComputeIntParam(shader, s_PropSrcWidth, srcWidth);
            cmd.SetComputeIntParam(shader, s_PropSrcHeight, srcHeight);
            cmd.SetComputeIntParam(shader, s_PropMipLevel, mipLevel);
            cmd.SetComputeIntParam(shader, s_PropBlockCountX, blockCountX);
            cmd.SetComputeIntParam(shader, s_PropBlockCountY, blockCountY);
            cmd.SetComputeIntParam(shader, s_PropBufferBlockOffset, bufferBlockOffset);
            cmd.SetComputeIntParam(shader, s_PropMode, (int)mode);
            cmd.SetComputeIntParam(shader, s_PropIsSRGB, isSRGB ? 1 : 0);
            cmd.SetComputeIntParam(shader, s_PropUseAlphaWeight, useAlphaWeight ? 1 : 0);
            cmd.SetComputeIntParam(shader, s_PropQuality, (int)qualityTier);
        }

        private static int CalculateTotalBlocks(int width, int height, int mipCount, uint blockX, uint blockY)
        {
            int total = 0;
            for (int i = 0; i < mipCount; i++)
            {
                int mipW = Math.Max(1, width >> i);
                int mipH = Math.Max(1, height >> i);
                int bx = (mipW + (int)blockX - 1) / (int)blockX;
                int by = (mipH + (int)blockY - 1) / (int)blockY;
                total += bx * by;
            }
            return total;
        }

        private static ComputeBuffer DispatchAllMipsToBuffer(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int mipCount,
            bool useAlphaWeight,
            out int totalBytes)
        {
            totalBytes = 0;
            if (!IsSupported || !IsInputSupported(source))
                return null;

            int slot = BlockSizeToSlot(blockSize);
            if (slot < 0 || !Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return null;

            int totalBlocks = CalculateTotalBlocks(source.width, source.height, mipCount, bx, by);
            if (totalBlocks <= 0)
                return null;

            uint mode = ResolveShaderMode(source.graphicsFormat, textureType, out bool isSRGB, out _);
            uint qualityTier = MapQualityTier(quality);
            int kernel = s_BufKernels[slot];

            var buffer = new ComputeBuffer(totalBlocks, k_BytesPerBlock, ComputeBufferType.Structured);
            try
            {
                int blockOffset = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int mipW = Math.Max(1, source.width >> mip);
                    int mipH = Math.Max(1, source.height >> mip);
                    int blockCountX = (mipW + (int)bx - 1) / (int)bx;
                    int blockCountY = (mipH + (int)by - 1) / (int)by;

                    s_Shader.SetTexture(kernel, s_PropSourceTex, source);
                    s_Shader.SetBuffer(kernel, s_PropDestBuffer, buffer);
                    SetCommonUniforms(s_Shader, mipW, mipH, mip, blockCountX, blockCountY, blockOffset, mode, isSRGB, useAlphaWeight, qualityTier);

                    int groupsX = (blockCountX + 7) >> 3;
                    int groupsY = (blockCountY + 7) >> 3;
                    s_Shader.Dispatch(kernel, groupsX, groupsY, 1);

                    blockOffset += blockCountX * blockCountY;
                }

                totalBytes = totalBlocks * k_BytesPerBlock;
                return buffer;
            }
            catch
            {
                buffer.Release();
                throw;
            }
        }

        [StructLayout(LayoutKind.Sequential, Size = k_BytesPerBlock)]
        private struct AstcBlock128
        {
            public uint W0;
            public uint W1;
            public uint W2;
            public uint W3;
        }

        internal static AstcRaw CompressTextureSync(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (!IsSupported)
            {
                Debug.LogError("GPU ASTC compression is not supported on this device.");
                return null;
            }

            if (!IsInputSupported(source))
            {
                Debug.LogError("Input texture is null or not a valid 2D texture for GPU ASTC compression.");
                return null;
            }

            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            ComputeBuffer buffer = DispatchAllMipsToBuffer(source, textureType, blockSize, quality, mipCount, useAlphaWeight, out int totalBytes);
            if (buffer == null || totalBytes <= 0)
                return null;

            try
            {
                int totalBlocks = totalBytes / k_BytesPerBlock;
                var blocks = new AstcBlock128[totalBlocks];
                buffer.GetData(blocks);

                IntPtr unmanaged = Marshal.AllocHGlobal(totalBytes);
                try
                {
                    unsafe
                    {
                        fixed (AstcBlock128* srcPtr = blocks)
                        {
                            UnsafeUtility.MemCpy((void*)unmanaged, srcPtr, totalBytes);
                        }
                    }
                    return new AstcRaw(unmanaged, totalBytes);
                }
                catch
                {
                    Marshal.FreeHGlobal(unmanaged);
                    throw;
                }
            }
            finally
            {
                buffer.Release();
            }
        }

        internal static Task<AstcRaw> CompressTextureAsync(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<AstcRaw>(cancellationToken);

            if (!IsSupported)
            {
                Debug.LogError("GPU ASTC compression is not supported on this device.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!IsInputSupported(source))
            {
                Debug.LogError("Input texture is null or not a valid 2D texture for GPU ASTC compression.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                return Task.FromResult(CompressTextureSync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight));
            }

            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            ComputeBuffer buffer = DispatchAllMipsToBuffer(source, textureType, blockSize, quality, mipCount, useAlphaWeight, out int totalBytes);
            if (buffer == null || totalBytes <= 0)
                return Task.FromResult<AstcRaw>(null);

            var tcs = new TaskCompletionSource<AstcRaw>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancelReg = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancelReg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            AsyncGPUReadback.Request(buffer, request =>
            {
                cancelReg.Dispose();
                try
                {
                    if (cancellationToken.IsCancellationRequested || tcs.Task.IsCanceled)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    if (request.hasError)
                    {
                        Debug.LogError("GPU ASTC async readback failed.");
                        tcs.TrySetResult(null);
                        return;
                    }

                    NativeArray<byte> data = request.GetData<byte>();
                    int byteLen = data.Length;
                    IntPtr unmanaged = Marshal.AllocHGlobal(byteLen);
                    AstcRaw raw = null;
                    try
                    {
                        unsafe
                        {
                            UnsafeUtility.MemCpy((void*)unmanaged, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data), byteLen);
                        }

                        // Ownership of the unmanaged block moves to AstcRaw from here on; never free it directly again.
                        raw = new AstcRaw(unmanaged, byteLen);
                        if (!tcs.TrySetResult(raw))
                        {
                            raw.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (raw != null)
                            raw.Dispose();
                        else
                            Marshal.FreeHGlobal(unmanaged);
                        tcs.TrySetException(ex);
                    }
                }
                finally
                {
                    buffer.Release();
                }
            });

            return tcs.Task;
        }

        internal static void Encode(
            CommandBuffer cmd,
            Texture source,
            Texture destination,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int sourceMip,
            int destMip,
            int destX,
            int destY,
            bool useAlphaWeight)
        {
            if (cmd == null)
                throw new ArgumentNullException(nameof(cmd));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (!IsSupported)
                throw new NotSupportedException("GPU ASTC compression is not supported on this device.");

            if (!IsInputSupported(source))
                throw new ArgumentException("Source texture is not a supported 2D texture for GPU ASTC encoding.", nameof(source));

            int slot = BlockSizeToSlot(blockSize);
            if (slot < 0 || !Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                throw new ArgumentException($"Unsupported ASTC block size: {blockSize}", nameof(blockSize));

            GraphicsFormat dstFormat = destination.graphicsFormat;
            if (dstFormat != GetAstcGraphicsFormat(blockSize, false, false)
                && dstFormat != GetAstcGraphicsFormat(blockSize, true, false)
                && dstFormat != GetAstcGraphicsFormat(blockSize, false, true))
            {
                throw new ArgumentException($"Destination texture format '{dstFormat}' does not match ASTC block size {blockSize}.", nameof(destination));
            }

            int srcMipCount = Math.Max(1, source.mipmapCount);
            if (sourceMip < 0 || sourceMip >= srcMipCount)
                throw new ArgumentOutOfRangeException(nameof(sourceMip), sourceMip, $"sourceMip must be in [0, {srcMipCount - 1}].");

            int dstMipCount = Math.Max(1, destination.mipmapCount);
            if (destMip < 0 || destMip >= dstMipCount)
                throw new ArgumentOutOfRangeException(nameof(destMip), destMip, $"destMip must be in [0, {dstMipCount - 1}].");

            int mipW = Math.Max(1, source.width >> sourceMip);
            int mipH = Math.Max(1, source.height >> sourceMip);
            int blockCountX = (mipW + (int)bx - 1) / (int)bx;
            int blockCountY = (mipH + (int)by - 1) / (int)by;

            int dstMipW = Math.Max(1, destination.width >> destMip);
            int dstMipH = Math.Max(1, destination.height >> destMip);
            int alignedDstW = ((dstMipW + (int)bx - 1) / (int)bx) * (int)bx;
            int alignedDstH = ((dstMipH + (int)by - 1) / (int)by) * (int)by;

            if (destX < 0 || (destX % (int)bx) != 0 || destX + blockCountX * (int)bx > alignedDstW)
                throw new ArgumentOutOfRangeException(nameof(destX), destX, $"destX must be a non-negative multiple of {bx} and fit within destination mip width {dstMipW}.");

            if (destY < 0 || (destY % (int)by) != 0 || destY + blockCountY * (int)by > alignedDstH)
                throw new ArgumentOutOfRangeException(nameof(destY), destY, $"destY must be a non-negative multiple of {by} and fit within destination mip height {dstMipH}.");

            uint mode = ResolveShaderMode(source.graphicsFormat, textureType, out bool isSRGB, out _);
            uint qualityTier = MapQualityTier(quality);
            int kernel = s_TexKernels[slot];

            // Temp RT lifetime is owned by the CommandBuffer, so it stays valid however late the buffer executes.
            var tempRT = new RenderTargetIdentifier(s_PropTempBlocksRT);
            cmd.GetTemporaryRT(s_PropTempBlocksRT, CreateBlockRTDescriptor(blockCountX, blockCountY), FilterMode.Point);

            cmd.SetComputeTextureParam(s_Shader, kernel, s_PropSourceTex, source);
            cmd.SetComputeTextureParam(s_Shader, kernel, s_PropDestTex, tempRT);
            SetCommonUniforms(cmd, s_Shader, mipW, mipH, sourceMip, blockCountX, blockCountY, 0, mode, isSRGB, useAlphaWeight, qualityTier);

            int groupsX = (blockCountX + 7) >> 3;
            int groupsY = (blockCountY + 7) >> 3;
            cmd.DispatchCompute(s_Shader, kernel, groupsX, groupsY, 1);

            cmd.CopyTexture(tempRT, 0, 0, 0, 0, blockCountX, blockCountY, destination, 0, destMip, destX, destY);
            cmd.ReleaseTemporaryRT(s_PropTempBlocksRT);
        }

        internal static Texture2D CompressToTexture(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (!IsSupported)
            {
                Debug.LogError("GPU ASTC compression is not supported on this device.");
                return null;
            }

            if (!IsInputSupported(source))
            {
                Debug.LogError("Input texture is null or not a valid 2D texture for GPU ASTC compression.");
                return null;
            }

            GraphicsFormat astcFormat = ResolveTargetAstcFormat(source.graphicsFormat, textureType, blockSize, out _, out _);
            if (!Encoder.CanCreateAstcTexture(astcFormat))
            {
                Debug.LogError($"ASTC texture format '{astcFormat}' is not supported on this device. Use CompressTexture or EncodeToASTC for raw ASTC data.");
                return null;
            }
            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;

            // Fast zero-readback path when all mip dimensions align cleanly and GPU natively samples ASTC
            if (CanUseDirectCopy(source.width, source.height, mipCount, blockSize, astcFormat))
            {
                TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
                if (mipCount > 1)
                    flags |= TextureCreationFlags.MipChain;

                var dst = new Texture2D(source.width, source.height, astcFormat, mipCount, flags);
                dst.name = source.name;
                dst.Apply(false, true);

                var cmd = new CommandBuffer { name = "AstcGpuCompressToTexture" };
                try
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        Encode(cmd, source, dst, textureType, blockSize, quality, mip, mip, 0, 0, useAlphaWeight);
                    }
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    cmd.Release();
                }

                return dst;
            }

            // Universal GPU path for non-aligned dimensions, full 1x1 mip tails, or non-ASTC desktop Editors (Unity software preview)
            using AstcRaw raw = CompressTextureSync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
            if (raw == null)
                return null;

            TextureCreationFlags fallbackFlags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var fallbackTex = new Texture2D(source.width, source.height, astcFormat, mipCount, fallbackFlags);
            fallbackTex.name = source.name;
            fallbackTex.LoadRawTextureData(raw);
            fallbackTex.Apply(false, true);
            return fallbackTex;
        }

        internal static bool CompressInPlace(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool makeNoLongerReadable,
            bool useAlphaWeight)
        {
            if (!IsSupported)
            {
                Debug.LogError("GPU ASTC compression is not supported on this device.");
                return false;
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Input Texture2D is null or invalid for GPU ASTC compression.");
                return false;
            }

            if (!inputTexture.isReadable)
            {
                Debug.LogError("In-place Compress requires Texture2D.isReadable == true (Unity Texture2D.Reinitialize/LoadRawTextureData requirement). Use CompressToTexture for non-readable textures.");
                return false;
            }

            GraphicsFormat astcFormat = ResolveTargetAstcFormat(inputTexture.graphicsFormat, textureType, blockSize, out _, out _);
            if (!Encoder.CanCreateAstcTexture(astcFormat))
            {
                Debug.LogError($"ASTC texture format '{astcFormat}' is not supported on this device. Use CompressTexture or EncodeToASTC for raw ASTC data.");
                return false;
            }
            int width = inputTexture.width;
            int height = inputTexture.height;
            int mipCount = Math.Max(1, inputTexture.mipmapCount);
            if (!Encoder.IsFullMipChain(width, height, mipCount))
            {
                Debug.LogError($"In-place Compress does not support partial mipmap chains (mipmapCount={mipCount} for {width}x{height}) because Texture2D.Reinitialize allocates a full mipmap chain. Use CompressToTexture instead.");
                return false;
            }

            // Fast zero-readback path: encode into temporary per-mip RTs BEFORE Reinitialize wipes source pixels.
            // Only valid when makeNoLongerReadable is true, because Graphics.CopyTexture writes GPU memory only
            // and would otherwise leave a readable Texture2D with an uninitialized CPU pixel buffer.
            if (makeNoLongerReadable
                && CanUseDirectCopy(width, height, mipCount, blockSize, astcFormat)
                && Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
            {
                int slot = BlockSizeToSlot(blockSize);
                uint mode = ResolveShaderMode(inputTexture.graphicsFormat, textureType, out bool isSRGB, out _);
                uint qualityTier = MapQualityTier(quality);
                int kernel = s_TexKernels[slot];

                var mipRTs = new RenderTexture[mipCount];
                try
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        int mipW = Math.Max(1, width >> mip);
                        int mipH = Math.Max(1, height >> mip);
                        int blockW = (mipW + (int)bx - 1) / (int)bx;
                        int blockH = (mipH + (int)by - 1) / (int)by;

                        var desc = new RenderTextureDescriptor(blockW, blockH, GraphicsFormat.R32G32B32A32_UInt, 0)
                        {
                            enableRandomWrite = true,
                            useMipMap = false,
                            autoGenerateMips = false,
                        };
                        RenderTexture rt = RenderTexture.GetTemporary(desc);
                        rt.Create();
                        mipRTs[mip] = rt;

                        s_Shader.SetTexture(kernel, s_PropSourceTex, inputTexture);
                        s_Shader.SetTexture(kernel, s_PropDestTex, rt);
                        SetCommonUniforms(s_Shader, mipW, mipH, mip, blockW, blockH, 0, mode, isSRGB, useAlphaWeight, qualityTier);
                        s_Shader.Dispatch(kernel, (blockW + 7) >> 3, (blockH + 7) >> 3, 1);
                    }

                    if (!inputTexture.Reinitialize(width, height, astcFormat, mipCount > 1))
                        return false;

                    inputTexture.Apply(false, makeNoLongerReadable);

                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        RenderTexture rt = mipRTs[mip];
                        Graphics.CopyTexture(rt, 0, 0, 0, 0, rt.width, rt.height, inputTexture, 0, mip, 0, 0);
                    }

                    return true;
                }
                finally
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        if (mipRTs[mip] != null)
                            RenderTexture.ReleaseTemporary(mipRTs[mip]);
                    }
                }
            }

            // Universal GPU fallback via ComputeBuffer + LoadRawTextureData
            using AstcRaw raw = CompressTextureSync(inputTexture, textureType, blockSize, quality, mipCount > 1, useAlphaWeight);
            if (raw == null)
                return false;

            if (inputTexture.Reinitialize(width, height, astcFormat, mipCount > 1))
            {
                inputTexture.LoadRawTextureData(raw);
                inputTexture.Apply(false, makeNoLongerReadable);
                return true;
            }

            return false;
        }
    }
}
