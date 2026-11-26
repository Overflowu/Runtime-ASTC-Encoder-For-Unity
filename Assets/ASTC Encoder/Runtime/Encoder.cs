using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Aperture.Astcenc.Runtime.NativeAPI;

namespace Aperture.Astcenc.Runtime
{
    public static class Encoder
    {
        private const int k_BytePerBlock = 16;

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

        internal static float GetQualityValue(CompressQuality quality)
        {
            return quality switch
            {
                CompressQuality.Fastest => 0.0f,
                CompressQuality.Fast => 10.0f,
                CompressQuality.Medium => 60.0f,
                CompressQuality.Thorough => 98.0f,
                CompressQuality.VeryThorough => 99.0f,
                CompressQuality.Exhaustive => 100.0f,
                _ => throw new ArgumentException("Unknown type of a CompressQuality:", nameof(quality)),
            };
        }

        internal static astcenc_profile GetColorProfile(GraphicsFormat graphicsFormat)
        {
            return graphicsFormat switch
            {
                //LDR

                GraphicsFormat.R8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,
                GraphicsFormat.R8G8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R8G8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,
                GraphicsFormat.R8G8B8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R8G8B8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,
                GraphicsFormat.R8G8B8A8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R8G8B8A8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,
                GraphicsFormat.B8G8R8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.B8G8R8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,
                GraphicsFormat.B8G8R8A8_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.B8G8R8A8_SRGB => astcenc_profile.ASTCENC_PRF_LDR_SRGB,

                GraphicsFormat.R16_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R16G16_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R16G16B16_UNorm => astcenc_profile.ASTCENC_PRF_LDR,
                GraphicsFormat.R16G16B16A16_UNorm => astcenc_profile.ASTCENC_PRF_LDR,

                //HDR

                GraphicsFormat.R16_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R16G16_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R16G16B16_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R16G16B16A16_SFloat => astcenc_profile.ASTCENC_PRF_HDR,

                GraphicsFormat.R32_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R32G32_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R32G32B32_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                GraphicsFormat.R32G32B32A32_SFloat => astcenc_profile.ASTCENC_PRF_HDR,
                _ => throw new NotSupportedException($"GraphicsFormat is not supported: {graphicsFormat}"),
            };
        }

        private static bool HasAlphaChannel(GraphicsFormat graphicsFormat)
        {
            switch (graphicsFormat)
            {
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.R16G16B16A16_UNorm:
                case GraphicsFormat.R16G16B16A16_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return true;
                default:
                    return false;
            }
        }

        internal static CodecFlags GetFlags(TextureType textureType, GraphicsFormat graphicsFormat, bool useAlphaWeight = false)
        {
            if (textureType == TextureType.Normal)
            {
                return CodecFlags.MapNormal | CodecFlags.SelfDecompressOnly;
            }

            CodecFlags flags = CodecFlags.SelfDecompressOnly;

            if (useAlphaWeight && HasAlphaChannel(graphicsFormat))
            {
                flags |= CodecFlags.UseAlphaWeight;
            }

            return flags;
        }

        private static bool IsBgrFormat(GraphicsFormat graphicsFormat)
        {
            switch (graphicsFormat)
            {
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    return true;
                default:
                    return false;
            }
        }

        private static astcenc_swizzle GetSwizzle(GraphicsFormat graphicsFormat, TextureType textureType)
        {
            bool isBgr = IsBgrFormat(graphicsFormat);

            if (textureType == TextureType.Normal)
            {
                astcenc_swizzle swizzle = new astcenc_swizzle
                {
                    r = astcenc_swz.ASTCENC_SWZ_G,
                    g = astcenc_swz.ASTCENC_SWZ_G,
                    b = astcenc_swz.ASTCENC_SWZ_G,
                    a = isBgr ? astcenc_swz.ASTCENC_SWZ_B : astcenc_swz.ASTCENC_SWZ_R,
                };
                return swizzle;
            }

            if (isBgr)
            {
                astcenc_swizzle swizzle = new astcenc_swizzle
                {
                    r = astcenc_swz.ASTCENC_SWZ_B,
                    g = astcenc_swz.ASTCENC_SWZ_G,
                    b = astcenc_swz.ASTCENC_SWZ_R,
                    a = astcenc_swz.ASTCENC_SWZ_A,
                };
                return swizzle;
            }

            return new astcenc_swizzle
            {
                r = astcenc_swz.ASTCENC_SWZ_R,
                g = astcenc_swz.ASTCENC_SWZ_G,
                b = astcenc_swz.ASTCENC_SWZ_B,
                a = astcenc_swz.ASTCENC_SWZ_A,
            };
        }

        private static GraphicsFormat FitAstcGraphicsFormat(GraphicsFormat graphicsFormat, BlockSize blockSize)
        {
            switch (graphicsFormat)
            {
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.R8G8B8A8_SRGB:

                case GraphicsFormat.B8G8R8_SRGB:
                case GraphicsFormat.B8G8R8A8_SRGB:
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

        internal static bool IsSupportGraphicFormat(GraphicsFormat graphicsFormat)
        {
            switch (graphicsFormat)
            {
                //LDR

                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:

                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:

                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16G16_UNorm:
                case GraphicsFormat.R16G16B16_UNorm:
                case GraphicsFormat.R16G16B16A16_UNorm:

                //HDR

                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R16G16_SFloat:
                case GraphicsFormat.R16G16B16_SFloat:
                case GraphicsFormat.R16G16B16A16_SFloat:

                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.R32G32_SFloat:
                case GraphicsFormat.R32G32B32_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return true;
            }
            return false;
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

        public static bool IsSupport(Texture texture)
        {
            return texture != null && texture.isReadable && IsSupportGraphicFormat(texture.graphicsFormat);
        }

        public static bool IsSupport(Texture texture, EncodeBackend backend)
        {
            return backend switch
            {
                EncodeBackend.Cpu => IsSupport(texture),
                EncodeBackend.Gpu => AstcGpuBackend.IsSupported && AstcGpuBackend.IsInputSupported(texture),
                EncodeBackend.Auto => (AstcGpuBackend.IsSupported && AstcGpuBackend.IsInputSupported(texture)) || IsSupport(texture),
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

        private static int CalculateMipByteLength(int mipWidth, int mipHeight, uint blockX, uint blockY)
        {
            int blockCountX = (mipWidth + (int)blockX - 1) / (int)blockX;
            int blockCountY = (mipHeight + (int)blockY - 1) / (int)blockY;
            return blockCountX * blockCountY * k_BytePerBlock;
        }

        private static bool CompressRawData<T>(AstcContext context, astcenc_swizzle swizzle, NativeArray<T> raw, int width, int height, int bitness, int mipCount, bool direct4Channel, out IntPtr output, out int size) where T : unmanaged, IRawData
        {
            uint blockX = context.BlockX;
            uint blockY = context.BlockY;

            //alloc a total memory to hold all the mipmaps
            int total = 0;
            for (int i = 0; i < mipCount; i++)
            {
                int mipmapWidth = Math.Max(1, width >> i);
                int mipmapHeight = Math.Max(1, height >> i);
                total += CalculateMipByteLength(mipmapWidth, mipmapHeight, blockX, blockY);
            }
            output = Marshal.AllocHGlobal(total);
            size = total;

            int elementsRead = 0;
            int bytesWritten = 0;

            try
            {
                for (int i = 0; i < mipCount; i++)
                {
                    int mipmapWidth = Math.Max(1, width >> i);
                    int mipmapHeight = Math.Max(1, height >> i);
                    int length = CalculateMipByteLength(mipmapWidth, mipmapHeight, blockX, blockY);

                    int count = mipmapWidth * mipmapHeight;
                    NativeSlice<T> mipmapSlice = raw.Slice(elementsRead, count);

                    astcenc_image image = new astcenc_image();
                    try
                    {
                        if (direct4Channel)
                        {
                            image.LoadFromDirect(mipmapSlice, mipmapWidth, mipmapHeight, bitness);
                        }
                        else
                        {
                            image.LoadFrom(mipmapSlice, mipmapWidth, mipmapHeight, bitness);
                        }

                        astcenc_error status = context.CompressSingleImage(ref image, ref swizzle, output + bytesWritten, length);
                        if (status != astcenc_error.ASTCENC_SUCCESS)
                        {
                            throw new Exception(status.ToString());
                        }
                    }
                    finally
                    {
                        image.Release(ownsSliceMemory: !direct4Channel);
                    }

                    elementsRead += count;
                    bytesWritten += length;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("ASTC compress failed: " + ex.Message);
                Marshal.FreeHGlobal(output);

                output = IntPtr.Zero;
                size = 0;

                return false;
            }
            return true;
        }

        private static astcenc_image[] SnapshotRawData<T>(NativeArray<T> raw, int width, int height, int bitness, int mipCount, bool direct4Channel) where T : unmanaged, IRawData
        {
            astcenc_image[] images = new astcenc_image[mipCount];
            int elementsRead = 0;
            try
            {
                for (int i = 0; i < mipCount; i++)
                {
                    int mipmapWidth = Math.Max(1, width >> i);
                    int mipmapHeight = Math.Max(1, height >> i);
                    int count = mipmapWidth * mipmapHeight;
                    NativeSlice<T> mipmapSlice = raw.Slice(elementsRead, count);

                    astcenc_image image = new astcenc_image();
                    if (direct4Channel)
                    {
                        image.LoadFromCopy(mipmapSlice, mipmapWidth, mipmapHeight, bitness);
                    }
                    else
                    {
                        image.LoadFrom(mipmapSlice, mipmapWidth, mipmapHeight, bitness);
                    }
                    images[i] = image;
                    elementsRead += count;
                }
                return images;
            }
            catch
            {
                ReleaseSnapshots(images);
                throw;
            }
        }

        private static void ReleaseSnapshots(astcenc_image[] images)
        {
            if (images == null)
                return;

            for (int i = 0; i < images.Length; i++)
            {
                images[i].Release(ownsSliceMemory: true);
            }
        }

        private static astcenc_image[] SnapshotTextureMipmaps(Texture2D inputTexture, int mipCount)
        {
            int width = inputTexture.width;
            int height = inputTexture.height;
            switch (inputTexture.graphicsFormat)
            {
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    return SnapshotRawData(inputTexture.GetRawTextureData<R8>(), width, height, 8, mipCount, false);
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RG8>(), width, height, 8, mipCount, false);
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGB8>(), width, height, 8, mipCount, false);
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGBA8>(), width, height, 8, mipCount, true);
                case GraphicsFormat.R16_UNorm:
                    return SnapshotRawData(inputTexture.GetRawTextureData<R16>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R16G16_UNorm:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RG16>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R16G16B16_UNorm:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGB16>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R16G16B16A16_UNorm:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGBA16>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R16_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<R16F>(), width, height, 16, mipCount, false);
                case GraphicsFormat.R16G16_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RG16F>(), width, height, 16, mipCount, false);
                case GraphicsFormat.R16G16B16_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGB16F>(), width, height, 16, mipCount, false);
                case GraphicsFormat.R16G16B16A16_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGBA16F>(), width, height, 16, mipCount, true);
                case GraphicsFormat.R32_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<R32F>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R32G32_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RG32F>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R32G32B32_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGB32F>(), width, height, 32, mipCount, false);
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return SnapshotRawData(inputTexture.GetRawTextureData<RGBA32F>(), width, height, 32, mipCount, true);
                default:
                    return null;
            }
        }

        private static AstcRaw CompressSnapshots(AstcContext context, astcenc_swizzle swizzle, astcenc_image[] mipmaps, CancellationToken cancellationToken)
        {
            uint blockX = context.BlockX;
            uint blockY = context.BlockY;

            int total = 0;
            for (int i = 0; i < mipmaps.Length; i++)
            {
                total += CalculateMipByteLength((int)mipmaps[i].dim_x, (int)mipmaps[i].dim_y, blockX, blockY);
            }

            IntPtr output = Marshal.AllocHGlobal(total);
            int bytesWritten = 0;
            try
            {
                for (int i = 0; i < mipmaps.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = CalculateMipByteLength((int)mipmaps[i].dim_x, (int)mipmaps[i].dim_y, blockX, blockY);
                    astcenc_error status = context.CompressSingleImage(ref mipmaps[i], ref swizzle, output + bytesWritten, length, cancellationToken);
                    if (status != astcenc_error.ASTCENC_SUCCESS)
                    {
                        throw new Exception(status.ToString());
                    }
                    bytesWritten += length;
                }

                return new AstcRaw(output, total);
            }
            catch (OperationCanceledException)
            {
                Marshal.FreeHGlobal(output);
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError("ASTC async compress failed: " + ex.Message);
                Marshal.FreeHGlobal(output);
                return null;
            }
        }

        private static bool CompressImage(Texture2D inputTexture, AstcContext context, astcenc_swizzle swizzle, int mipCount, out IntPtr output, out int size)
        {
            int width = inputTexture.width;
            int height = inputTexture.height;
            switch (inputTexture.graphicsFormat)
            {
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    {
                        NativeArray<R8> raw = inputTexture.GetRawTextureData<R8>();
                        return CompressRawData(context, swizzle, raw, width, height, 8, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                    {
                        NativeArray<RG8> raw = inputTexture.GetRawTextureData<RG8>();
                        return CompressRawData(context, swizzle, raw, width, height, 8, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                    {
                        NativeArray<RGB8> raw = inputTexture.GetRawTextureData<RGB8>();
                        return CompressRawData(context, swizzle, raw, width, height, 8, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    {
                        NativeArray<RGBA8> raw = inputTexture.GetRawTextureData<RGBA8>();
                        return CompressRawData(context, swizzle, raw, width, height, 8, mipCount, true, out output, out size);
                    }
                case GraphicsFormat.R16_UNorm:
                    {
                        NativeArray<R16> raw = inputTexture.GetRawTextureData<R16>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16_UNorm:
                    {
                        NativeArray<RG16> raw = inputTexture.GetRawTextureData<RG16>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16B16_UNorm:
                    {
                        NativeArray<RGB16> raw = inputTexture.GetRawTextureData<RGB16>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16B16A16_UNorm:
                    {
                        NativeArray<RGBA16> raw = inputTexture.GetRawTextureData<RGBA16>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16_SFloat:
                    {
                        NativeArray<R16F> raw = inputTexture.GetRawTextureData<R16F>();
                        return CompressRawData(context, swizzle, raw, width, height, 16, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16_SFloat:
                    {
                        NativeArray<RG16F> raw = inputTexture.GetRawTextureData<RG16F>();
                        return CompressRawData(context, swizzle, raw, width, height, 16, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16B16_SFloat:
                    {
                        NativeArray<RGB16F> raw = inputTexture.GetRawTextureData<RGB16F>();
                        return CompressRawData(context, swizzle, raw, width, height, 16, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R16G16B16A16_SFloat:
                    {
                        NativeArray<RGBA16F> raw = inputTexture.GetRawTextureData<RGBA16F>();
                        return CompressRawData(context, swizzle, raw, width, height, 16, mipCount, true, out output, out size);
                    }
                case GraphicsFormat.R32_SFloat:
                    {
                        NativeArray<R32F> raw = inputTexture.GetRawTextureData<R32F>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R32G32_SFloat:
                    {
                        NativeArray<RG32F> raw = inputTexture.GetRawTextureData<RG32F>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R32G32B32_SFloat:
                    {
                        NativeArray<RGB32F> raw = inputTexture.GetRawTextureData<RGB32F>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, false, out output, out size);
                    }
                case GraphicsFormat.R32G32B32A32_SFloat:
                    {
                        NativeArray<RGBA32F> raw = inputTexture.GetRawTextureData<RGBA32F>();
                        return CompressRawData(context, swizzle, raw, width, height, 32, mipCount, true, out output, out size);
                    }
            }

            output = IntPtr.Zero;
            size = 0;

            return false;
        }

        internal static AstcRaw CompressTextureWithContext(Texture2D inputTexture, AstcContext context, TextureType textureType, bool includeMipmaps)
        {
            if (!IsSupport(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return null;
            }

            if (context == null)
            {
                Debug.LogError("AstcContext is null.");
                return null;
            }

            context.ThrowIfDisposed();

            if (!context.IsCompatibleWith(inputTexture.graphicsFormat, textureType))
            {
                Debug.LogError("AstcContext profile or flags do not match the input texture format and type.");
                return null;
            }

            astcenc_swizzle swizzle = GetSwizzle(inputTexture.graphicsFormat, textureType);
            int mipCount = includeMipmaps ? inputTexture.mipmapCount : 1;

            bool success = CompressImage(inputTexture, context, swizzle, mipCount, out IntPtr output, out int size);
            if (!success || output == IntPtr.Zero || size <= 0)
            {
                if (output != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(output);
                }
                return null;
            }

            return new AstcRaw(output, size);
        }

        internal static Task<AstcRaw> CompressTextureWithContextAsync(Texture2D inputTexture, AstcContext context, TextureType textureType, bool includeMipmaps, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AstcRaw>(cancellationToken);
            }

            if (!IsSupport(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (context == null)
            {
                Debug.LogError("AstcContext is null.");
                return Task.FromResult<AstcRaw>(null);
            }

            context.ThrowIfDisposed();

            if (!context.IsCompatibleWith(inputTexture.graphicsFormat, textureType))
            {
                Debug.LogError("AstcContext profile or flags do not match the input texture format and type.");
                return Task.FromResult<AstcRaw>(null);
            }

            astcenc_swizzle swizzle = GetSwizzle(inputTexture.graphicsFormat, textureType);
            int mipCount = includeMipmaps ? inputTexture.mipmapCount : 1;

            astcenc_image[] snapshots;
            try
            {
                snapshots = SnapshotTextureMipmaps(inputTexture, mipCount);
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to snapshot texture mipmaps for async compression: " + ex.Message);
                return Task.FromResult<AstcRaw>(null);
            }

            if (snapshots == null)
            {
                return Task.FromResult<AstcRaw>(null);
            }

            return Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CompressSnapshots(context, swizzle, snapshots, cancellationToken);
                }
                finally
                {
                    ReleaseSnapshots(snapshots);
                }
            });
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
        /// <param name="backend">Execution backend (Cpu, Gpu, or Auto)</param>
        /// <returns>CompressedData</returns>
        public static AstcRaw CompressTexture(this Texture2D inputTexture, TextureType textureType, BlockSize blockSize, CompressQuality quality, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Cpu)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight);
            }

            if (!IsSupport(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return null;
            }

            using AstcContext context = AstcContext.Create(inputTexture.graphicsFormat, textureType, blockSize, quality, threadCount, useAlphaWeight);
            if (context == null)
            {
                return null;
            }

            return CompressTextureWithContext(inputTexture, context, textureType, true);
        }

        /// <summary>
        /// Compresses a RenderTexture on the GPU and returns raw ASTC bytes. Call Dispose() on the returned AstcRaw after use.
        /// </summary>
        public static AstcRaw CompressTexture(this RenderTexture inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool includeMipmaps = true, bool useAlphaWeight = false)
        {
            return AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);
        }

        /// <summary>
        /// Compresses a Texture2D asynchronously on CPU background thread(s) or via GPU AsyncGPUReadback depending on backend.
        /// You should call Dispose() on the returned AstcRaw after use.
        /// </summary>
        /// <param name="inputTexture">Input texture (CPU backend requires readable Texture2D; GPU backend supports non-readable Texture2D)</param>
        /// <param name="textureType">Indicate that compress for color texture or normal texture</param>
        /// <param name="blockSize">Astc compress ratio</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="threadCount">Worker thread count for CPU backend (&lt;= 0 = auto based on CPU cores)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures; leave false for channel-packed maps)</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="backend">Execution backend (Cpu, Gpu, or Auto)</param>
        /// <returns>Task yielding compressed AstcRaw data or null on failure</returns>
        public static Task<AstcRaw> CompressTextureAsync(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, int threadCount = 0, bool useAlphaWeight = false, CancellationToken cancellationToken = default, EncodeBackend backend = EncodeBackend.Cpu)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AstcRaw>(cancellationToken);
            }

            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressTextureAsync(inputTexture, textureType, blockSize, quality, includeMipmaps: true, useAlphaWeight: useAlphaWeight, cancellationToken: cancellationToken);
            }

            if (!IsSupport(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!GetBlockSize(blockSize, out _, out _, out _))
            {
                Debug.LogError("Unsupported ASTC block size: " + blockSize);
                return Task.FromResult<AstcRaw>(null);
            }

            GraphicsFormat graphicsFormat = inputTexture.graphicsFormat;
            astcenc_swizzle swizzle = GetSwizzle(graphicsFormat, textureType);
            int mipCount = inputTexture.mipmapCount;

            astcenc_image[] snapshots;
            try
            {
                snapshots = SnapshotTextureMipmaps(inputTexture, mipCount);
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to snapshot texture mipmaps for async compression: " + ex.Message);
                return Task.FromResult<AstcRaw>(null);
            }

            if (snapshots == null)
            {
                return Task.FromResult<AstcRaw>(null);
            }

            return Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using AstcContext context = AstcContext.Create(graphicsFormat, textureType, blockSize, quality, threadCount, useAlphaWeight);
                    if (context == null)
                    {
                        return null;
                    }

                    return CompressSnapshots(context, swizzle, snapshots, cancellationToken);
                }
                finally
                {
                    ReleaseSnapshots(snapshots);
                }
            });
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
        /// <param name="inputTexture">Input texture (CPU backend requires readable Texture2D; GPU backend supports non-readable Texture2D)</param>
        /// <param name="textureType">Indicate that compress for color texture or normal texture</param>
        /// <param name="blockSize">Astc compress ratio</param>
        /// <param name="quality">Compress quality setting</param>
        /// <param name="makeNoLongerReadable">When set to true, Unity discards the copy of pixel data in CPU-addressable memory after this operation.</param>
        /// <param name="threadCount">Worker thread count for CPU backend (1 = single-threaded, &lt;= 0 = auto)</param>
        /// <param name="useAlphaWeight">Enable alpha-weighted RGB error optimization (only for transparent textures; leave false for channel-packed maps)</param>
        /// <param name="backend">Execution backend (Cpu, Gpu, or Auto)</param>
        /// <returns>Success or not</returns>
        public static bool Compress(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Fast, bool makeNoLongerReadable = true, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Cpu)
        {
            if (ShouldUseGpu(inputTexture, backend))
            {
                return AstcGpuBackend.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, useAlphaWeight);
            }

            using AstcRaw raw = inputTexture.CompressTexture(textureType, blockSize, quality, threadCount, useAlphaWeight, EncodeBackend.Cpu);

            if (raw != null)
            {
                raw.ThrowIfDisposed();

                GraphicsFormat graphicsFormat = FitAstcGraphicsFormat(inputTexture.graphicsFormat, blockSize);

                if (inputTexture.Reinitialize(inputTexture.width, inputTexture.height, graphicsFormat, inputTexture.mipmapCount > 1))
                {
                    inputTexture.LoadRawTextureData(raw);
                    inputTexture.Apply(false, makeNoLongerReadable);

                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Compresses any 2D Texture (Texture2D or RenderTexture, without requiring Read/Write on GPU)
        /// into a newly allocated ASTC-compressed Texture2D. Uses zero-readback GPU CopyTexture when supported.
        /// </summary>
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
                if (!IsSupport(tex2d))
                {
                    Debug.LogError("Texture format is not support or read/write is disabled.");
                    return null;
                }

                using AstcContext context = AstcContext.Create(tex2d.graphicsFormat, textureType, blockSize, quality, 0, useAlphaWeight);
                if (context == null)
                {
                    return null;
                }

                using AstcRaw raw = CompressTextureWithContext(tex2d, context, textureType, includeMipmaps);
                if (raw == null)
                {
                    return null;
                }

                GraphicsFormat astcFormat = FitAstcGraphicsFormat(tex2d.graphicsFormat, blockSize);
                int mipCount = includeMipmaps ? tex2d.mipmapCount : 1;
                TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
                var result = new Texture2D(tex2d.width, tex2d.height, astcFormat, mipCount, flags);
                result.name = tex2d.name;
                result.LoadRawTextureData(raw);
                result.Apply(false, true);
                return result;
            }

            Debug.LogError("CPU backend only supports readable Texture2D. Use EncodeBackend.Gpu or EncodeBackend.Auto for RenderTexture inputs.");
            return null;
        }

        /// <summary>
        /// Records a GPU ASTC compression dispatch and zero-readback CopyTexture into a CommandBuffer.
        /// Ideal for batching, Async Compute queues, procedural textures, and virtual texture atlas tile updates.
        /// </summary>
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
        /// <param name="backend">Execution backend (Cpu, Gpu, or Auto)</param>
        /// <returns>.astc file bytes or null on failure</returns>
        public static byte[] EncodeToASTC(this Texture2D inputTexture, TextureType textureType = TextureType.Default, BlockSize blockSize = BlockSize.ASTC_6x6, CompressQuality quality = CompressQuality.Medium, int threadCount = 1, bool useAlphaWeight = false, EncodeBackend backend = EncodeBackend.Cpu)
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input texture is null.");
                return null;
            }

            AstcRaw raw;
            if (ShouldUseGpu(inputTexture, backend))
            {
                raw = AstcGpuBackend.CompressTextureSync(inputTexture, textureType, blockSize, quality, includeMipmaps: false, useAlphaWeight: useAlphaWeight);
            }
            else
            {
                if (!IsSupport(inputTexture))
                {
                    Debug.LogError("Texture format is not support or read/write is disabled.");
                    return null;
                }

                using AstcContext context = AstcContext.Create(inputTexture.graphicsFormat, textureType, blockSize, quality, threadCount, useAlphaWeight);
                if (context == null)
                {
                    return null;
                }

                // Standard .astc file header describes a single image level (mip 0 only)
                raw = CompressTextureWithContext(inputTexture, context, textureType, includeMipmaps: false);
            }

            using (raw)
            {
                return BuildAstcFileBytes(raw, blockSize, inputTexture.width, inputTexture.height);
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
            }
            else if (inputTexture is Texture2D tex2d)
            {
                if (!IsSupport(tex2d))
                {
                    Debug.LogError("Texture format is not support or read/write is disabled.");
                    return null;
                }

                if (!GetBlockSize(blockSize, out _, out _, out _))
                {
                    Debug.LogError("Unsupported ASTC block size: " + blockSize);
                    return null;
                }

                GraphicsFormat graphicsFormat = tex2d.graphicsFormat;
                astcenc_swizzle swizzle = GetSwizzle(graphicsFormat, textureType);
                astcenc_image[] snapshots = SnapshotTextureMipmaps(tex2d, 1);
                if (snapshots == null)
                {
                    return null;
                }

                raw = await Task.Run(() =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using AstcContext context = AstcContext.Create(graphicsFormat, textureType, blockSize, quality, threadCount, useAlphaWeight);
                        if (context == null)
                            return null;

                        return CompressSnapshots(context, swizzle, snapshots, cancellationToken);
                    }
                    finally
                    {
                        ReleaseSnapshots(snapshots);
                    }
                });
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

