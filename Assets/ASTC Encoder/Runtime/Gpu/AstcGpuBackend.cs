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
        private const string k_MipShaderResourcePath = "AstcGpuMipGenerator";
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

        private static readonly int s_PropMipSrcTex = Shader.PropertyToID("_MipSrcTex");
        private static readonly int s_PropMipDstTex = Shader.PropertyToID("_MipDstTex");
        private static readonly int s_PropMipSrcWidth = Shader.PropertyToID("_MipSrcWidth");
        private static readonly int s_PropMipSrcHeight = Shader.PropertyToID("_MipSrcHeight");
        private static readonly int s_PropMipDstWidth = Shader.PropertyToID("_MipDstWidth");
        private static readonly int s_PropMipDstHeight = Shader.PropertyToID("_MipDstHeight");
        private static readonly int s_PropMipSrcLevel = Shader.PropertyToID("_MipSrcLevel");
        private static readonly int s_PropMipIsSRGBSrc = Shader.PropertyToID("_MipIsSRGBSrc");
        private static readonly int s_PropMipIsAlphaWeighted = Shader.PropertyToID("_MipIsAlphaWeighted");
        private static readonly int s_PropMipIsHdr = Shader.PropertyToID("_MipIsHdr");

        private static ComputeShader s_Shader;
        private static bool s_ShaderLoadAttempted;
        private static int[] s_TexKernels;
        private static int[] s_BufKernels;
        private static int[] s_TexFastKernels;
        private static int[] s_BufFastKernels;

        private static ComputeShader s_MipShader;
        private static bool s_MipShaderLoadAttempted;
        private static int s_MipKernelColor;
        private static int s_MipKernelNormal;

        private static readonly int s_PropTempBlocksRT = Shader.PropertyToID("_AstcTempBlocksRT");
        private static readonly int s_PropTempSliceRT = Shader.PropertyToID("_AstcTempSliceRT");

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
            return IsInputSupportedCore(texture, allowSliced: false);
        }

        internal static bool IsSliceableInputSupported(Texture texture)
        {
            return IsInputSupportedCore(texture, allowSliced: true);
        }

        private static bool IsInputSupportedCore(Texture texture, bool allowSliced)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return false;

            if (texture.dimension != TextureDimension.Tex2D)
            {
                if (!allowSliced || (texture.dimension != TextureDimension.Tex2DArray && texture.dimension != TextureDimension.Cube))
                    return false;
            }

            if (texture is RenderTexture rt && rt.antiAliasing > 1)
                return false;

            GraphicsFormat format = texture.graphicsFormat;
            if (format == GraphicsFormat.None)
                return false;

            // _AstcSourceTex is bound as Texture2D<float4>; depth/stencil/integer formats cannot be loaded as float4,
            // and signed normalized (SNorm) formats would have negative values silently clamped to 0 by UNorm ASTC.
            if (GraphicsFormatUtility.IsDepthFormat(format)
                || GraphicsFormatUtility.IsStencilFormat(format)
                || GraphicsFormatUtility.IsIntegerFormat(format)
                || (!GraphicsFormatUtility.IsFloatFormat(format) && GraphicsFormatUtility.IsSignedFormat(format)))
            {
                return false;
            }

            return true;
        }

        internal static bool SupportsDirectCopy(BlockSize blockSize, bool srgb, bool isHdr)
        {
            if (!IsSupported)
                return false;

            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) == 0)
                return false;

            GraphicsFormat astcFormat = GetAstcGraphicsFormat(blockSize, srgb, isHdr);
            return IsSampleSupported(astcFormat);
        }

        internal static bool CanUseDirectCopy(int width, int height, int mipCount, BlockSize blockSize, GraphicsFormat astcFormat)
        {
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) == 0)
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
            if (textureType == TextureType.Normal)
            {
                // Tangent-space normal maps always emit linear UNorm ASTC regardless of sRGB or float source format.
                isSRGB = false;
                isHdr = false;
                return GetAstcGraphicsFormat(blockSize, false, false);
            }

            bool sourceIsSRGB = GraphicsFormatUtility.IsSRGBFormat(sourceFormat);
            isHdr = !sourceIsSRGB && GraphicsFormatUtility.IsFloatFormat(sourceFormat);
            isSRGB = sourceIsSRGB;
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

        private static int GetTexKernel(int slot, uint qualityTier)
        {
            return qualityTier == 0u ? s_TexFastKernels[slot] : s_TexKernels[slot];
        }

        private static int GetBufKernel(int slot, uint qualityTier)
        {
            return qualityTier == 0u ? s_BufFastKernels[slot] : s_BufKernels[slot];
        }

        private static uint ResolveShaderMode(GraphicsFormat sourceFormat, TextureType textureType, out bool isSRGB, out bool isHdr)
        {
            isSRGB = GraphicsFormatUtility.IsSRGBFormat(sourceFormat);

            if (textureType == TextureType.Normal)
            {
                isHdr = false;
                return k_ModeLdrNormal;
            }

            isHdr = !isSRGB && GraphicsFormatUtility.IsFloatFormat(sourceFormat);
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

            s_TexFastKernels = new int[6]
            {
                s_Shader.FindKernel("EncodeTex4x4Fast"),
                s_Shader.FindKernel("EncodeTex5x5Fast"),
                s_Shader.FindKernel("EncodeTex6x6Fast"),
                s_Shader.FindKernel("EncodeTex8x8Fast"),
                s_Shader.FindKernel("EncodeTex10x10Fast"),
                s_Shader.FindKernel("EncodeTex12x12Fast"),
            };

            s_BufFastKernels = new int[6]
            {
                s_Shader.FindKernel("EncodeBuf4x4Fast"),
                s_Shader.FindKernel("EncodeBuf5x5Fast"),
                s_Shader.FindKernel("EncodeBuf6x6Fast"),
                s_Shader.FindKernel("EncodeBuf8x8Fast"),
                s_Shader.FindKernel("EncodeBuf10x10Fast"),
                s_Shader.FindKernel("EncodeBuf12x12Fast"),
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
            ComputeBuffer buf = null;
            try
            {
                rt = RenderTexture.GetTemporary(CreateBlockRTDescriptor(1, 1));
                rt.Create();
                buf = new ComputeBuffer(1, k_BytesPerBlock, ComputeBufferType.Structured);

                // Warm up both Fast and Full kernels for Texture and Buffer outputs to prevent first-use PSO stalls.
                int texFastKernel = s_TexFastKernels[slot];
                s_Shader.SetTexture(texFastKernel, s_PropSourceTex, dummySrc);
                s_Shader.SetTexture(texFastKernel, s_PropDestTex, rt);
                SetCommonUniforms(s_Shader, 4, 4, 0, 1, 1, 0, k_ModeLdrRgb, false, false, 0u);
                s_Shader.Dispatch(texFastKernel, 1, 1, 1);

                int bufFastKernel = s_BufFastKernels[slot];
                s_Shader.SetTexture(bufFastKernel, s_PropSourceTex, dummySrc);
                s_Shader.SetBuffer(bufFastKernel, s_PropDestBuffer, buf);
                SetCommonUniforms(s_Shader, 4, 4, 0, 1, 1, 0, k_ModeLdrRgb, false, false, 0u);
                s_Shader.Dispatch(bufFastKernel, 1, 1, 1);

                int texKernel = s_TexKernels[slot];
                s_Shader.SetTexture(texKernel, s_PropSourceTex, dummySrc);
                s_Shader.SetTexture(texKernel, s_PropDestTex, rt);
                SetCommonUniforms(s_Shader, 4, 4, 0, 1, 1, 0, k_ModeLdrRgb, false, false, 1u);
                s_Shader.Dispatch(texKernel, 1, 1, 1);

                int bufKernel = s_BufKernels[slot];
                s_Shader.SetTexture(bufKernel, s_PropSourceTex, dummySrc);
                s_Shader.SetBuffer(bufKernel, s_PropDestBuffer, buf);
                SetCommonUniforms(s_Shader, 4, 4, 0, 1, 1, 0, k_ModeLdrRgb, false, false, 1u);
                s_Shader.Dispatch(bufKernel, 1, 1, 1);

                if (EnsureMipShaderLoaded())
                {
                    RenderTexture mipRt = RenderTexture.GetTemporary(CreateMipRTDescriptor(2, 2, useFloat16: false));
                    try
                    {
                        mipRt.Create();
                        s_MipShader.SetInt(s_PropMipSrcWidth, 4);
                        s_MipShader.SetInt(s_PropMipSrcHeight, 4);
                        s_MipShader.SetInt(s_PropMipDstWidth, 2);
                        s_MipShader.SetInt(s_PropMipDstHeight, 2);
                        s_MipShader.SetInt(s_PropMipSrcLevel, 0);
                        s_MipShader.SetInt(s_PropMipIsSRGBSrc, 0);
                        s_MipShader.SetInt(s_PropMipIsAlphaWeighted, 0);
                        s_MipShader.SetInt(s_PropMipIsHdr, 0);

                        s_MipShader.SetTexture(s_MipKernelColor, s_PropMipSrcTex, dummySrc);
                        s_MipShader.SetTexture(s_MipKernelColor, s_PropMipDstTex, mipRt);
                        s_MipShader.Dispatch(s_MipKernelColor, 1, 1, 1);

                        s_MipShader.SetTexture(s_MipKernelNormal, s_PropMipSrcTex, dummySrc);
                        s_MipShader.SetTexture(s_MipKernelNormal, s_PropMipDstTex, mipRt);
                        s_MipShader.Dispatch(s_MipKernelNormal, 1, 1, 1);
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(mipRt);
                    }
                }
            }
            finally
            {
                if (buf != null)
                    buf.Release();
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
        private static bool EnsureMipShaderLoaded()
        {
            if (s_MipShader != null)
                return true;

            if (s_MipShaderLoadAttempted)
                return false;

            s_MipShaderLoadAttempted = true;
            s_MipShader = Resources.Load<ComputeShader>(k_MipShaderResourcePath);
            if (s_MipShader == null)
            {
                Debug.LogError($"Failed to load ComputeShader from Resources/{k_MipShaderResourcePath}.compute.");
                return false;
            }

            s_MipKernelColor = s_MipShader.FindKernel("DownsampleColor");
            s_MipKernelNormal = s_MipShader.FindKernel("DownsampleNormal");
            return true;
        }

        internal static int CalculateFullMipCount(int width, int height)
        {
            int maxDim = Math.Max(1, Math.Max(width, height));
            int count = 1;
            while (maxDim > 1)
            {
                maxDim >>= 1;
                count++;
            }
            return count;
        }

        private static int ResolveEffectiveMipCount(Texture source, bool includeMipmaps, bool generateMipmaps, out bool willGenerateMips)
        {
            willGenerateMips = false;
            int srcMips = Math.Max(1, source.mipmapCount);
            if (generateMipmaps && srcMips <= 1 && (source.width > 1 || source.height > 1) && EnsureMipShaderLoaded())
            {
                bool isHdrSrc = GraphicsFormatUtility.IsFloatFormat(source.graphicsFormat);
                if (!isHdrSrc || IsLoadStoreSupported(GraphicsFormat.R16G16B16A16_SFloat))
                {
                    willGenerateMips = true;
                    return CalculateFullMipCount(source.width, source.height);
                }
            }

            return includeMipmaps ? srcMips : 1;
        }

        private static RenderTextureDescriptor CreateMipRTDescriptor(int width, int height, bool useFloat16)
        {
            GraphicsFormat fmt = useFloat16 && IsLoadStoreSupported(GraphicsFormat.R16G16B16A16_SFloat)
                ? GraphicsFormat.R16G16B16A16_SFloat
                : GraphicsFormat.R8G8B8A8_UNorm;

            return new RenderTextureDescriptor(width, height, fmt, 0)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
        }

        internal static void ReleaseCache()
        {
            s_Shader = null;
            s_ShaderLoadAttempted = false;
            s_TexKernels = null;
            s_BufKernels = null;
            s_TexFastKernels = null;
            s_BufFastKernels = null;
            s_MipShader = null;
            s_MipShaderLoadAttempted = false;
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
            bool generateMipmaps,
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

            uint mode = ResolveShaderMode(source.graphicsFormat, textureType, out bool isSRGB, out bool isHdr);
            uint qualityTier = MapQualityTier(quality);
            int kernel = GetBufKernel(slot, qualityTier);

            bool useFloat16Mip = (isHdr || isSRGB) && textureType != TextureType.Normal;
            int mipKernel = textureType == TextureType.Normal ? s_MipKernelNormal : s_MipKernelColor;

            var buffer = new ComputeBuffer(totalBlocks, k_BytesPerBlock, ComputeBufferType.Structured);
            RenderTexture prevMipRT = null;
            RenderTexture currMipRT = null;
            try
            {
                int blockOffset = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int mipW = Math.Max(1, source.width >> mip);
                    int mipH = Math.Max(1, source.height >> mip);
                    int blockCountX = (mipW + (int)bx - 1) / (int)bx;
                    int blockCountY = (mipH + (int)by - 1) / (int)by;

                    Texture encodeSrc = source;
                    int encodeSrcMip = mip;
                    bool encodeIsSRGB = isSRGB;

                    if (generateMipmaps && mip > 0)
                    {
                        int prevW = Math.Max(1, source.width >> (mip - 1));
                        int prevH = Math.Max(1, source.height >> (mip - 1));
                        Texture downSrc = (mip == 1) ? source : (Texture)prevMipRT;
                        bool downSrcIsSRGB = (mip == 1) && isSRGB;

                        currMipRT = RenderTexture.GetTemporary(CreateMipRTDescriptor(mipW, mipH, useFloat16Mip));
                        currMipRT.Create();

                        s_MipShader.SetTexture(mipKernel, s_PropMipSrcTex, downSrc);
                        s_MipShader.SetTexture(mipKernel, s_PropMipDstTex, currMipRT);
                        s_MipShader.SetInt(s_PropMipSrcWidth, prevW);
                        s_MipShader.SetInt(s_PropMipSrcHeight, prevH);
                        s_MipShader.SetInt(s_PropMipDstWidth, mipW);
                        s_MipShader.SetInt(s_PropMipDstHeight, mipH);
                        s_MipShader.SetInt(s_PropMipSrcLevel, 0);
                        s_MipShader.SetInt(s_PropMipIsSRGBSrc, downSrcIsSRGB ? 1 : 0);
                        s_MipShader.SetInt(s_PropMipIsAlphaWeighted, useAlphaWeight ? 1 : 0);
                        s_MipShader.SetInt(s_PropMipIsHdr, isHdr ? 1 : 0);
                        s_MipShader.Dispatch(mipKernel, (mipW + 7) >> 3, (mipH + 7) >> 3, 1);

                        if (prevMipRT != null)
                        {
                            RenderTexture.ReleaseTemporary(prevMipRT);
                        }
                        prevMipRT = currMipRT;
                        currMipRT = null;

                        encodeSrc = prevMipRT;
                        encodeSrcMip = 0;
                        // Normal downsampler outputs linear UNorm [0,1]; sRGB color downsampler keeps linear FP16 so encoder still converts to sRGB.
                        encodeIsSRGB = isSRGB && textureType != TextureType.Normal;
                    }

                    s_Shader.SetTexture(kernel, s_PropSourceTex, encodeSrc);
                    s_Shader.SetBuffer(kernel, s_PropDestBuffer, buffer);
                    SetCommonUniforms(s_Shader, mipW, mipH, encodeSrcMip, blockCountX, blockCountY, blockOffset, mode, encodeIsSRGB, useAlphaWeight, qualityTier);

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
            finally
            {
                if (currMipRT != null)
                    RenderTexture.ReleaseTemporary(currMipRT);
                if (prevMipRT != null)
                    RenderTexture.ReleaseTemporary(prevMipRT);
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
            bool useAlphaWeight,
            bool generateMipmaps = false)
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

            int mipCount = ResolveEffectiveMipCount(source, includeMipmaps, generateMipmaps, out bool willGenMips);
            ComputeBuffer buffer = DispatchAllMipsToBuffer(source, textureType, blockSize, quality, mipCount, useAlphaWeight, willGenMips, out int totalBytes);
            if (buffer == null || totalBytes <= 0)
                return null;

            try
            {
                // Avoid LOH managed array allocation when AsyncGPUReadback is available.
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(buffer);
                    req.WaitForCompletion();
                    if (req.hasError)
                    {
                        Debug.LogError("GPU ASTC readback failed.");
                        return null;
                    }

                    NativeArray<byte> data = req.GetData<byte>();
                    int byteLen = data.Length;
                    IntPtr unmanaged = Marshal.AllocHGlobal(byteLen);
                    try
                    {
                        unsafe
                        {
                            UnsafeUtility.MemCpy((void*)unmanaged, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data), byteLen);
                        }
                        return new AstcRaw(unmanaged, byteLen);
                    }
                    catch
                    {
                        Marshal.FreeHGlobal(unmanaged);
                        throw;
                    }
                }

                int totalBlocks = totalBytes / k_BytesPerBlock;
                var blocks = new AstcBlock128[totalBlocks];
                buffer.GetData(blocks);

                IntPtr fallbackUnmanaged = Marshal.AllocHGlobal(totalBytes);
                try
                {
                    unsafe
                    {
                        fixed (AstcBlock128* srcPtr = blocks)
                        {
                            UnsafeUtility.MemCpy((void*)fallbackUnmanaged, srcPtr, totalBytes);
                        }
                    }
                    return new AstcRaw(fallbackUnmanaged, totalBytes);
                }
                catch
                {
                    Marshal.FreeHGlobal(fallbackUnmanaged);
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
            CancellationToken cancellationToken,
            bool generateMipmaps = false)
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
                return Task.FromResult(CompressTextureSync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps));
            }

            int mipCount = ResolveEffectiveMipCount(source, includeMipmaps, generateMipmaps, out bool willGenMips);
            ComputeBuffer buffer = DispatchAllMipsToBuffer(source, textureType, blockSize, quality, mipCount, useAlphaWeight, willGenMips, out int totalBytes);
            if (buffer == null || totalBytes <= 0)
                return Task.FromResult<AstcRaw>(null);

            var tcs = new TaskCompletionSource<AstcRaw>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancelReg = default;
            if (cancellationToken.CanBeCanceled)
            {
                cancelReg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            try
            {
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
            }
            catch
            {
                cancelReg.Dispose();
                buffer.Release();
                throw;
            }

            return tcs.Task;
        }

        private static void ValidateSliceIndex(Texture texture, int slice, string paramName)
        {
            if (slice < 0)
                throw new ArgumentOutOfRangeException(paramName, slice, $"{paramName} must be non-negative.");

            if (texture.dimension == TextureDimension.Tex2D && slice != 0)
                throw new ArgumentOutOfRangeException(paramName, slice, $"{paramName} must be 0 for a 2D texture.");

            if (texture.dimension == TextureDimension.Cube && slice >= 6)
                throw new ArgumentOutOfRangeException(paramName, slice, $"{paramName} must be in [0, 5] for a Cubemap.");

            if (texture is Texture2DArray texArray && slice >= texArray.depth)
                throw new ArgumentOutOfRangeException(paramName, slice, $"{paramName} must be in [0, {texArray.depth - 1}] for Texture2DArray.");
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
            bool useAlphaWeight,
            int sourceSlice = 0,
            int destSlice = 0)
        {
            if (cmd == null)
                throw new ArgumentNullException(nameof(cmd));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            ValidateSliceIndex(source, sourceSlice, nameof(sourceSlice));
            ValidateSliceIndex(destination, destSlice, nameof(destSlice));

            if (!IsSupported)
                throw new NotSupportedException("GPU ASTC compression is not supported on this device.");

            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) == 0)
                throw new NotSupportedException("GPU direct CopyTexture from RenderTexture to Texture is not supported on this device.");

            if (!IsSliceableInputSupported(source))
                throw new ArgumentException("Source texture is not a supported 2D, Texture2DArray, or Cubemap texture for GPU ASTC encoding.", nameof(source));

            int slot = BlockSizeToSlot(blockSize);
            if (slot < 0 || !Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                throw new ArgumentException($"Unsupported ASTC block size: {blockSize}", nameof(blockSize));

            GraphicsFormat expectedFormat = ResolveTargetAstcFormat(source.graphicsFormat, textureType, blockSize, out _, out _);
            GraphicsFormat dstFormat = destination.graphicsFormat;
            if (dstFormat != expectedFormat)
            {
                throw new ArgumentException($"Destination texture format '{dstFormat}' does not match expected ASTC format '{expectedFormat}' for source format '{source.graphicsFormat}', textureType '{textureType}', and blockSize '{blockSize}'.", nameof(destination));
            }

            if (!IsSampleSupported(dstFormat))
                throw new NotSupportedException($"Destination ASTC format '{dstFormat}' is not natively supported by this GPU.");

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
            int kernel = GetTexKernel(slot, qualityTier);

            // Temp RT lifetime is owned by the CommandBuffer, so it stays valid however late the buffer executes.
            var tempRT = new RenderTargetIdentifier(s_PropTempBlocksRT);
            cmd.GetTemporaryRT(s_PropTempBlocksRT, CreateBlockRTDescriptor(blockCountX, blockCountY), FilterMode.Point);

            bool needsSliceExtract = source.dimension != TextureDimension.Tex2D;
            if (needsSliceExtract)
            {
                var sliceDesc = new RenderTextureDescriptor(mipW, mipH, source.graphicsFormat, 0)
                {
                    enableRandomWrite = false,
                    useMipMap = false,
                    autoGenerateMips = false,
                    msaaSamples = 1,
                };
                var tempSliceRT = new RenderTargetIdentifier(s_PropTempSliceRT);
                cmd.GetTemporaryRT(s_PropTempSliceRT, sliceDesc, FilterMode.Point);
                cmd.CopyTexture(source, sourceSlice, sourceMip, 0, 0, mipW, mipH, tempSliceRT, 0, 0, 0, 0);
                cmd.SetComputeTextureParam(s_Shader, kernel, s_PropSourceTex, tempSliceRT);
                SetCommonUniforms(cmd, s_Shader, mipW, mipH, 0, blockCountX, blockCountY, 0, mode, isSRGB, useAlphaWeight, qualityTier);
            }
            else
            {
                cmd.SetComputeTextureParam(s_Shader, kernel, s_PropSourceTex, source);
                SetCommonUniforms(cmd, s_Shader, mipW, mipH, sourceMip, blockCountX, blockCountY, 0, mode, isSRGB, useAlphaWeight, qualityTier);
            }

            cmd.SetComputeTextureParam(s_Shader, kernel, s_PropDestTex, tempRT);

            int groupsX = (blockCountX + 7) >> 3;
            int groupsY = (blockCountY + 7) >> 3;
            cmd.DispatchCompute(s_Shader, kernel, groupsX, groupsY, 1);

            cmd.CopyTexture(tempRT, 0, 0, 0, 0, blockCountX, blockCountY, destination, destSlice, destMip, destX, destY);
            cmd.ReleaseTemporaryRT(s_PropTempBlocksRT);
            if (needsSliceExtract)
            {
                cmd.ReleaseTemporaryRT(s_PropTempSliceRT);
            }
        }

        internal static Cubemap CompressCubemap(
            Cubemap source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (!IsSupported || !IsSliceableInputSupported(source) || source.dimension != TextureDimension.Cube)
            {
                Debug.LogError("GPU ASTC Cubemap compression requires a valid Cubemap and GPU compute support.");
                return null;
            }

            GraphicsFormat astcFormat = ResolveTargetAstcFormat(source.graphicsFormat, textureType, blockSize, out _, out _);
            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            if (!CanUseDirectCopy(source.width, source.height, mipCount, blockSize, astcFormat))
            {
                Debug.LogError($"GPU direct ASTC Cubemap compression requires hardware ASTC support and block-aligned dimensions for '{astcFormat}'.");
                return null;
            }

            TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
            if (mipCount > 1)
                flags |= TextureCreationFlags.MipChain;

            var dst = new Cubemap(source.width, astcFormat, flags, mipCount);
            var cmd = new CommandBuffer { name = "AstcGpuCompressCubemap" };
            try
            {
                dst.name = source.name;
                for (int face = 0; face < 6; face++)
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        Encode(cmd, source, dst, textureType, blockSize, quality, mip, mip, 0, 0, useAlphaWeight, face, face);
                    }
                }
                Graphics.ExecuteCommandBuffer(cmd);
                return dst;
            }
            catch (Exception ex)
            {
                Debug.LogError($"GPU ASTC Cubemap compression failed: {ex.Message}");
                UnityEngine.Object.DestroyImmediate(dst);
                return null;
            }
            finally
            {
                cmd.Release();
            }
        }

        internal static Texture2DArray CompressTexture2DArray(
            Texture2DArray source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (!IsSupported || !IsSliceableInputSupported(source) || source.dimension != TextureDimension.Tex2DArray)
            {
                Debug.LogError("GPU ASTC Texture2DArray compression requires a valid Texture2DArray and GPU compute support.");
                return null;
            }

            GraphicsFormat astcFormat = ResolveTargetAstcFormat(source.graphicsFormat, textureType, blockSize, out _, out _);
            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            if (!CanUseDirectCopy(source.width, source.height, mipCount, blockSize, astcFormat))
            {
                Debug.LogError($"GPU direct ASTC Texture2DArray compression requires hardware ASTC support and block-aligned dimensions for '{astcFormat}'.");
                return null;
            }

            TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
            if (mipCount > 1)
                flags |= TextureCreationFlags.MipChain;

            int depth = source.depth;
            var dst = new Texture2DArray(source.width, source.height, depth, astcFormat, flags, mipCount);
            var cmd = new CommandBuffer { name = "AstcGpuCompressTexture2DArray" };
            try
            {
                dst.name = source.name;
                for (int slice = 0; slice < depth; slice++)
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        Encode(cmd, source, dst, textureType, blockSize, quality, mip, mip, 0, 0, useAlphaWeight, slice, slice);
                    }
                }
                Graphics.ExecuteCommandBuffer(cmd);
                return dst;
            }
            catch (Exception ex)
            {
                Debug.LogError($"GPU ASTC Texture2DArray compression failed: {ex.Message}");
                UnityEngine.Object.DestroyImmediate(dst);
                return null;
            }
            finally
            {
                cmd.Release();
            }
        }

        internal static Texture2D CompressToTexture(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            bool generateMipmaps = false)
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
            int mipCount = ResolveEffectiveMipCount(source, includeMipmaps, generateMipmaps, out bool willGenMips);

            // Fast zero-readback path when all mip dimensions align cleanly, no generated mips are needed, and GPU natively samples ASTC
            if (!willGenMips && CanUseDirectCopy(source.width, source.height, mipCount, blockSize, astcFormat))
            {
                TextureCreationFlags flags = TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate;
                if (mipCount > 1)
                    flags |= TextureCreationFlags.MipChain;

                var dst = new Texture2D(source.width, source.height, astcFormat, mipCount, flags);
                var cmd = new CommandBuffer { name = "AstcGpuCompressToTexture" };
                try
                {
                    dst.name = source.name;
                    dst.Apply(false, true);

                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        Encode(cmd, source, dst, textureType, blockSize, quality, mip, mip, 0, 0, useAlphaWeight);
                    }
                    Graphics.ExecuteCommandBuffer(cmd);
                    return dst;
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(dst);
                    throw;
                }
                finally
                {
                    cmd.Release();
                }
            }

            // Universal GPU path for generated mipmaps, non-aligned dimensions, full 1x1 mip tails, or non-ASTC desktop Editors
            using AstcRaw raw = CompressTextureSync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps);
            if (raw == null)
                return null;

            TextureCreationFlags fallbackFlags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var fallbackTex = new Texture2D(source.width, source.height, astcFormat, mipCount, fallbackFlags);
            try
            {
                fallbackTex.name = source.name;
                fallbackTex.LoadRawTextureData(raw);
                fallbackTex.Apply(false, true);
                return fallbackTex;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(fallbackTex);
                throw;
            }
        }

        internal static async Task<Texture2D> CompressToTextureAsync(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            CancellationToken cancellationToken,
            bool generateMipmaps = false)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

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
                Debug.LogError($"ASTC texture format '{astcFormat}' is not supported on this device. Use CompressTextureAsync or EncodeToASTCAsync for raw ASTC data.");
                return null;
            }

            int width = source.width;
            int height = source.height;
            int mipCount = ResolveEffectiveMipCount(source, includeMipmaps, generateMipmaps, out bool willGenMips);
            string texName = source.name;

            // Zero-readback GPU-only path is already non-blocking on the CPU.
            if (!willGenMips && CanUseDirectCopy(width, height, mipCount, blockSize, astcFormat))
            {
                return CompressToTexture(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, false);
            }

            // Non-blocking AsyncGPUReadback path for generated mipmaps, mipmapped, or non-block-aligned textures.
            using AstcRaw raw = await CompressTextureAsync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken, generateMipmaps);
            if (raw == null)
                return null;

            cancellationToken.ThrowIfCancellationRequested();

            TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var result = new Texture2D(width, height, astcFormat, mipCount, flags);
            try
            {
                result.name = texName;
                result.LoadRawTextureData(raw);
                result.Apply(false, true);
                return result;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(result);
                throw;
            }
        }

        internal static bool CompressInPlace(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool makeNoLongerReadable,
            bool useAlphaWeight,
            bool generateMipmaps = false)
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
            int mipCount = ResolveEffectiveMipCount(inputTexture, includeMipmaps: true, generateMipmaps, out bool willGenMips);
            if (!Encoder.IsFullMipChain(width, height, mipCount))
            {
                Debug.LogError($"In-place Compress does not support partial mipmap chains (mipmapCount={mipCount} for {width}x{height}) because Texture2D.Reinitialize allocates a full mipmap chain. Use CompressToTexture instead.");
                return false;
            }

            // Fast zero-readback path: encode into temporary per-mip RTs BEFORE Reinitialize wipes source pixels.
            // Only valid when makeNoLongerReadable is true, because Graphics.CopyTexture writes GPU memory only
            // and would otherwise leave a readable Texture2D with an uninitialized CPU pixel buffer.
            if (!willGenMips
                && makeNoLongerReadable
                && CanUseDirectCopy(width, height, mipCount, blockSize, astcFormat)
                && Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
            {
                int slot = BlockSizeToSlot(blockSize);
                uint mode = ResolveShaderMode(inputTexture.graphicsFormat, textureType, out bool isSRGB, out _);
                uint qualityTier = MapQualityTier(quality);
                int kernel = GetTexKernel(slot, qualityTier);

                var mipRTs = new RenderTexture[mipCount];
                try
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        int mipW = Math.Max(1, width >> mip);
                        int mipH = Math.Max(1, height >> mip);
                        int blockW = (mipW + (int)bx - 1) / (int)bx;
                        int blockH = (mipH + (int)by - 1) / (int)by;

                        RenderTexture rt = RenderTexture.GetTemporary(CreateBlockRTDescriptor(blockW, blockH));
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
            using AstcRaw raw = CompressTextureSync(inputTexture, textureType, blockSize, quality, mipCount > 1, useAlphaWeight, generateMipmaps);
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
