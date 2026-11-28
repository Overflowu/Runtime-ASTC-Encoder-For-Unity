using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Aperture.Astcenc.Runtime.NativeAPI;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Native CPU ASTC compression backend powered by ARM astc-encoder (P/Invoke).
    /// Supports single-threaded, multi-threaded, and async compression with reusable AstcContext.
    /// </summary>
    internal static class AstcCpuBackend
    {
        private const int k_BytePerBlock = 16;

        internal static bool IsSupported => astcenc_api.IsNativeLibraryAvailable;

        internal static bool IsSupportGraphicFormat(GraphicsFormat graphicsFormat)
        {
            switch (graphicsFormat)
            {
                // LDR
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

                // HDR
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

        internal static bool IsInputSupported(Texture texture)
        {
            return texture is Texture2D && texture.isReadable && IsSupportGraphicFormat(texture.graphicsFormat);
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

        internal static astcenc_profile GetColorProfile(GraphicsFormat graphicsFormat, TextureType textureType = TextureType.Default)
        {
            astcenc_profile profile = graphicsFormat switch
            {
                // LDR
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

                // HDR
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

            if (textureType == TextureType.Normal)
            {
                // Tangent-space normal maps are always encoded as LDR linear regardless of sRGB or float source format.
                return astcenc_profile.ASTCENC_PRF_LDR;
            }

            return profile;
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

        internal static CodecFlags GetFlags(TextureType textureType, GraphicsFormat graphicsFormat, bool useAlphaWeight = false, bool usePerceptual = false)
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

            if (usePerceptual)
            {
                flags |= CodecFlags.UsePerceptual;
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
                return new astcenc_swizzle
                {
                    r = astcenc_swz.ASTCENC_SWZ_G,
                    g = astcenc_swz.ASTCENC_SWZ_G,
                    b = astcenc_swz.ASTCENC_SWZ_G,
                    a = isBgr ? astcenc_swz.ASTCENC_SWZ_B : astcenc_swz.ASTCENC_SWZ_R,
                };
            }

            if (isBgr)
            {
                return new astcenc_swizzle
                {
                    r = astcenc_swz.ASTCENC_SWZ_B,
                    g = astcenc_swz.ASTCENC_SWZ_G,
                    b = astcenc_swz.ASTCENC_SWZ_R,
                    a = astcenc_swz.ASTCENC_SWZ_A,
                };
            }

            return new astcenc_swizzle
            {
                r = astcenc_swz.ASTCENC_SWZ_R,
                g = astcenc_swz.ASTCENC_SWZ_G,
                b = astcenc_swz.ASTCENC_SWZ_B,
                a = astcenc_swz.ASTCENC_SWZ_A,
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

            // Allocate total memory to hold all mipmaps
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

        /// <summary>
        /// Compact raw pixel snapshot copied via a single memcpy on the main thread.
        /// Format expansion to 4-channel RGBA happens lazily one mip at a time on the worker thread,
        /// avoiding main-thread conversion stalls and reducing peak memory by up to 8x.
        /// </summary>
        private sealed class CompactTextureSnapshot : IDisposable
        {
            public IntPtr Buffer;
            public GraphicsFormat Format;
            public int Width;
            public int Height;
            public int MipCount;

            public void Dispose()
            {
                IntPtr ptr = Interlocked.Exchange(ref Buffer, IntPtr.Zero);
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        private static int GetSourcePixelByteSize(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    return 1;
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16_SFloat:
                    return 2;
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                    return 3;
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.R16G16_UNorm:
                case GraphicsFormat.R16G16_SFloat:
                case GraphicsFormat.R32_SFloat:
                    return 4;
                case GraphicsFormat.R16G16B16_UNorm:
                case GraphicsFormat.R16G16B16_SFloat:
                    return 6;
                case GraphicsFormat.R16G16B16A16_UNorm:
                case GraphicsFormat.R16G16B16A16_SFloat:
                case GraphicsFormat.R32G32_SFloat:
                    return 8;
                case GraphicsFormat.R32G32B32_SFloat:
                    return 12;
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return 16;
                default:
                    return 0;
            }
        }

        private static unsafe CompactTextureSnapshot CaptureCompactSnapshot(Texture2D inputTexture, int mipCount)
        {
            GraphicsFormat format = inputTexture.graphicsFormat;
            int pixelBytes = GetSourcePixelByteSize(format);
            if (pixelBytes <= 0)
                return null;

            int width = inputTexture.width;
            int height = inputTexture.height;
            long totalBytes = 0;
            for (int i = 0; i < mipCount; i++)
            {
                int mipW = Math.Max(1, width >> i);
                int mipH = Math.Max(1, height >> i);
                totalBytes += (long)mipW * mipH * pixelBytes;
            }

            NativeArray<byte> rawBytes = inputTexture.GetRawTextureData<byte>();
            if (totalBytes <= 0 || totalBytes > rawBytes.Length)
            {
                throw new InvalidOperationException($"Texture raw data length ({rawBytes.Length}) is smaller than required ({totalBytes}).");
            }

            IntPtr buffer = Marshal.AllocHGlobal((IntPtr)totalBytes);
            try
            {
                UnsafeUtility.MemCpy((void*)buffer, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rawBytes), totalBytes);
                return new CompactTextureSnapshot
                {
                    Buffer = buffer,
                    Format = format,
                    Width = width,
                    Height = height,
                    MipCount = mipCount,
                };
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }

        private static unsafe AstcRaw CompressCompactTyped<T>(
            AstcContext context,
            astcenc_swizzle swizzle,
            CompactTextureSnapshot snapshot,
            int bitness,
            bool direct4Channel,
            CancellationToken cancellationToken) where T : unmanaged, IRawData
        {
            uint blockX = context.BlockX;
            uint blockY = context.BlockY;
            int width = snapshot.Width;
            int height = snapshot.Height;
            int mipCount = snapshot.MipCount;

            int total = 0;
            for (int i = 0; i < mipCount; i++)
            {
                int mipW = Math.Max(1, width >> i);
                int mipH = Math.Max(1, height >> i);
                total += CalculateMipByteLength(mipW, mipH, blockX, blockY);
            }

            IntPtr output = Marshal.AllocHGlobal(total);
            long elementsRead = 0;
            int bytesWritten = 0;
            T* basePtr = (T*)snapshot.Buffer;

            try
            {
                for (int i = 0; i < mipCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int mipW = Math.Max(1, width >> i);
                    int mipH = Math.Max(1, height >> i);
                    int length = CalculateMipByteLength(mipW, mipH, blockX, blockY);
                    int count = mipW * mipH;
                    T* mipPtr = basePtr + elementsRead;

                    astcenc_image image = new astcenc_image();
                    try
                    {
                        if (direct4Channel)
                        {
                            image.LoadFromDirectPtr(mipPtr, mipW, mipH, bitness);
                        }
                        else
                        {
                            image.LoadFromPtr(mipPtr, mipW, mipH, bitness);
                        }

                        astcenc_error status = context.CompressSingleImage(ref image, ref swizzle, output + bytesWritten, length, cancellationToken);
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

        private static AstcRaw CompressCompactSnapshot(AstcContext context, astcenc_swizzle swizzle, CompactTextureSnapshot snapshot, CancellationToken cancellationToken)
        {
            switch (snapshot.Format)
            {
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    return CompressCompactTyped<R8>(context, swizzle, snapshot, 8, false, cancellationToken);
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                    return CompressCompactTyped<RG8>(context, swizzle, snapshot, 8, false, cancellationToken);
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                    return CompressCompactTyped<RGB8>(context, swizzle, snapshot, 8, false, cancellationToken);
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    return CompressCompactTyped<RGBA8>(context, swizzle, snapshot, 8, true, cancellationToken);
                case GraphicsFormat.R16_UNorm:
                    return CompressCompactTyped<R16>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R16G16_UNorm:
                    return CompressCompactTyped<RG16>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R16G16B16_UNorm:
                    return CompressCompactTyped<RGB16>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R16G16B16A16_UNorm:
                    return CompressCompactTyped<RGBA16>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R16_SFloat:
                    return CompressCompactTyped<R16F>(context, swizzle, snapshot, 16, false, cancellationToken);
                case GraphicsFormat.R16G16_SFloat:
                    return CompressCompactTyped<RG16F>(context, swizzle, snapshot, 16, false, cancellationToken);
                case GraphicsFormat.R16G16B16_SFloat:
                    return CompressCompactTyped<RGB16F>(context, swizzle, snapshot, 16, false, cancellationToken);
                case GraphicsFormat.R16G16B16A16_SFloat:
                    return CompressCompactTyped<RGBA16F>(context, swizzle, snapshot, 16, true, cancellationToken);
                case GraphicsFormat.R32_SFloat:
                    return CompressCompactTyped<R32F>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R32G32_SFloat:
                    return CompressCompactTyped<RG32F>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R32G32B32_SFloat:
                    return CompressCompactTyped<RGB32F>(context, swizzle, snapshot, 32, false, cancellationToken);
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return CompressCompactTyped<RGBA32F>(context, swizzle, snapshot, 32, true, cancellationToken);
                default:
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
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (!IsInputSupported(inputTexture))
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

            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!IsInputSupported(inputTexture))
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

            CompactTextureSnapshot snapshot;
            try
            {
                snapshot = CaptureCompactSnapshot(inputTexture, mipCount);
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to snapshot texture mipmaps for async compression: " + ex.Message);
                return Task.FromResult<AstcRaw>(null);
            }

            if (snapshot == null)
            {
                return Task.FromResult<AstcRaw>(null);
            }

            return RunCpuTask(() =>
            {
                using (snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CompressCompactSnapshot(context, swizzle, snapshot, cancellationToken);
                }
            }, cancellationToken);
        }

        private static Task<AstcRaw> RunCpuTask(Func<AstcRaw> work, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(work());
            }
            catch (OperationCanceledException)
            {
                return Task.FromCanceled<AstcRaw>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            }
            catch (Exception ex)
            {
                return Task.FromException<AstcRaw>(ex);
            }
#else
            return Task.Run(work, cancellationToken);
#endif
        }

        internal static AstcRaw CompressTextureSync(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount = 1,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false)
        {
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return null;
            }

            using AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(
                inputTexture.graphicsFormat,
                textureType,
                blockSize,
                quality,
                threadCount,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                useUnityJobSystem);
            if (lease.Context == null)
            {
                return null;
            }

            return CompressTextureWithContext(inputTexture, lease.Context, textureType, includeMipmaps);
        }

        internal static Task<AstcRaw> CompressTextureAsync(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount = 0,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            CancellationToken cancellationToken = default,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<AstcRaw>(cancellationToken);
            }

            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (!Encoder.GetBlockSize(blockSize, out _, out _, out _))
            {
                Debug.LogError("Unsupported ASTC block size: " + blockSize);
                return Task.FromResult<AstcRaw>(null);
            }

            GraphicsFormat graphicsFormat = inputTexture.graphicsFormat;
            astcenc_swizzle swizzle = GetSwizzle(graphicsFormat, textureType);
            int mipCount = includeMipmaps ? inputTexture.mipmapCount : 1;

            CompactTextureSnapshot snapshot;
            try
            {
                snapshot = CaptureCompactSnapshot(inputTexture, mipCount);
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to snapshot texture mipmaps for async compression: " + ex.Message);
                return Task.FromResult<AstcRaw>(null);
            }

            if (snapshot == null)
            {
                return Task.FromResult<AstcRaw>(null);
            }

            return RunCpuTask(() =>
            {
                using (snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(
                        graphicsFormat,
                        textureType,
                        blockSize,
                        quality,
                        threadCount,
                        useAlphaWeight,
                        usePerceptual,
                        alphaScaleRadius);
                    if (lease.Context == null)
                    {
                        return null;
                    }

                    return CompressCompactSnapshot(lease.Context, swizzle, snapshot, cancellationToken);
                }
            }, cancellationToken);
        }

        internal static Texture2D CompressToTexture(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            int threadCount = 0,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false)
        {
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return null;
            }

            GraphicsFormat astcFormat = Encoder.FitAstcGraphicsFormat(inputTexture.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(astcFormat))
            {
                Debug.LogError($"ASTC texture format '{astcFormat}' is not supported on this device. Use CompressTexture or EncodeToASTC for raw ASTC data.");
                return null;
            }

            using AstcRaw raw = CompressTextureSync(
                inputTexture,
                textureType,
                blockSize,
                quality,
                threadCount: threadCount,
                includeMipmaps: includeMipmaps,
                useAlphaWeight: useAlphaWeight,
                usePerceptual: usePerceptual,
                alphaScaleRadius: alphaScaleRadius,
                useUnityJobSystem: useUnityJobSystem);
            if (raw == null)
            {
                return null;
            }

            int mipCount = includeMipmaps ? inputTexture.mipmapCount : 1;
            TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var result = new Texture2D(inputTexture.width, inputTexture.height, astcFormat, mipCount, flags);
            try
            {
                result.name = inputTexture.name;
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

        internal static async Task<Texture2D> CompressToTextureAsync(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps = true,
            bool useAlphaWeight = false,
            int threadCount = 0,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return null;
            }

            GraphicsFormat astcFormat = Encoder.FitAstcGraphicsFormat(inputTexture.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(astcFormat))
            {
                Debug.LogError($"ASTC texture format '{astcFormat}' is not supported on this device. Use CompressTextureAsync or EncodeToASTCAsync for raw ASTC data.");
                return null;
            }

            int width = inputTexture.width;
            int height = inputTexture.height;
            int mipCount = includeMipmaps ? Math.Max(1, inputTexture.mipmapCount) : 1;
            string texName = inputTexture.name;

            using AstcRaw raw = await CompressTextureAsync(
                inputTexture,
                textureType,
                blockSize,
                quality,
                threadCount: threadCount,
                includeMipmaps: includeMipmaps,
                useAlphaWeight: useAlphaWeight,
                cancellationToken: cancellationToken,
                usePerceptual: usePerceptual,
                alphaScaleRadius: alphaScaleRadius);
            if (raw == null)
            {
                return null;
            }

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
            bool makeNoLongerReadable = true,
            int threadCount = 1,
            bool useAlphaWeight = false,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false)
        {
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return false;
            }

            if (!IsInputSupported(inputTexture))
            {
                Debug.LogError("Texture format is not support or read/write is disabled.");
                return false;
            }

            GraphicsFormat graphicsFormat = Encoder.FitAstcGraphicsFormat(inputTexture.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(graphicsFormat))
            {
                Debug.LogError($"ASTC texture format '{graphicsFormat}' is not supported on this device. Use CompressTexture or EncodeToASTC for raw ASTC data.");
                return false;
            }

            int mipCount = Math.Max(1, inputTexture.mipmapCount);
            if (!Encoder.IsFullMipChain(inputTexture.width, inputTexture.height, mipCount))
            {
                Debug.LogError($"In-place Compress does not support partial mipmap chains (mipmapCount={mipCount} for {inputTexture.width}x{inputTexture.height}) because Texture2D.Reinitialize allocates a full mipmap chain. Use CompressToTexture instead.");
                return false;
            }

            using AstcRaw raw = CompressTextureSync(
                inputTexture,
                textureType,
                blockSize,
                quality,
                threadCount: threadCount,
                includeMipmaps: true,
                useAlphaWeight: useAlphaWeight,
                usePerceptual: usePerceptual,
                alphaScaleRadius: alphaScaleRadius,
                useUnityJobSystem: useUnityJobSystem);
            if (raw == null)
            {
                return false;
            }

            raw.ThrowIfDisposed();

            if (inputTexture.Reinitialize(inputTexture.width, inputTexture.height, graphicsFormat, inputTexture.mipmapCount > 1))
            {
                inputTexture.LoadRawTextureData(raw);
                inputTexture.Apply(false, makeNoLongerReadable);
                return true;
            }

            return false;
        }

        internal static byte[] DecompressToBytes(
            IntPtr compressedData,
            int compressedLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            int threadCount = 1)
        {
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC library is not available on this platform.");
                return null;
            }

            if (compressedData == IntPtr.Zero || compressedLength <= 0 || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                Debug.LogError("Invalid ASTC decompression input arguments.");
                return null;
            }

            if (outputBitness != 8 && outputBitness != 16 && outputBitness != 32)
            {
                Debug.LogError($"Unsupported ASTC decompression outputBitness: {outputBitness}. Expected 8, 16, or 32.");
                return null;
            }

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
            {
                Debug.LogError("Unsupported ASTC block size: " + blockSize);
                return null;
            }

            long requiredBytesLong = ((width + (long)bx - 1L) / (long)bx) * ((height + (long)by - 1L) / (long)by) * k_BytePerBlock;
            if (requiredBytesLong <= 0 || requiredBytesLong > int.MaxValue || compressedLength < requiredBytesLong)
            {
                Debug.LogError($"Compressed ASTC buffer ({compressedLength} bytes) is smaller than required ({requiredBytesLong} bytes) for {width}x{height} {blockSize}.");
                return null;
            }
            int requiredBytes = (int)requiredBytesLong;

            using AstcContextPool.DecompressorLease lease = AstcContextPool.RentDecompressor(blockSize, srgb, isHdr, threadCount);
            if (lease.Context == null)
                return null;

            astcenc_swizzle swizzle = new astcenc_swizzle
            {
                r = astcenc_swz.ASTCENC_SWZ_R,
                g = astcenc_swz.ASTCENC_SWZ_G,
                b = astcenc_swz.ASTCENC_SWZ_B,
                a = astcenc_swz.ASTCENC_SWZ_A,
            };

            astcenc_image imageOut = new astcenc_image();
            try
            {
                imageOut.AllocateOutput(width, height, outputBitness, out IntPtr slicePtr, out int byteCount);
                astcenc_error status = lease.Context.DecompressSingleImage(compressedData, requiredBytes, ref imageOut, ref swizzle);
                if (status != astcenc_error.ASTCENC_SUCCESS)
                {
                    Debug.LogError("ASTC decompress failed: " + status);
                    return null;
                }

                byte[] result = new byte[byteCount];
                Marshal.Copy(slicePtr, result, 0, byteCount);
                return result;
            }
            finally
            {
                imageOut.Release(ownsSliceMemory: true);
            }
        }

        internal static unsafe byte[] DecompressToBytes(
            byte[] compressedBytes,
            int offset,
            int length,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            int threadCount = 1)
        {
            if (compressedBytes == null || offset < 0 || length <= 0 || offset + length > compressedBytes.Length)
            {
                Debug.LogError("Invalid compressed byte array range.");
                return null;
            }

            fixed (byte* ptr = &compressedBytes[offset])
            {
                return DecompressToBytes((IntPtr)ptr, length, width, height, blockSize, srgb, isHdr, outputBitness, threadCount);
            }
        }

        internal static Texture2D DecompressToTexture(
            IntPtr compressedData,
            int compressedLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int threadCount = 1,
            bool makeNoLongerReadable = false)
        {
            int bitness = isHdr ? 16 : 8;
            byte[] pixels = DecompressToBytes(compressedData, compressedLength, width, height, blockSize, srgb, isHdr, bitness, threadCount);
            if (pixels == null)
                return null;

            GraphicsFormat dstFormat = isHdr
                ? GraphicsFormat.R16G16B16A16_SFloat
                : (srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm);

            var tex = new Texture2D(width, height, dstFormat, TextureCreationFlags.None);
            try
            {
                tex.LoadRawTextureData(pixels);
                tex.Apply(false, makeNoLongerReadable);
                return tex;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(tex);
                throw;
            }
        }

        internal static unsafe Texture2D DecompressAstcFileToTexture(
            byte[] astcFileBytes,
            bool srgb = false,
            bool isHdr = false,
            int threadCount = 1,
            bool makeNoLongerReadable = false)
        {
            if (!AstcHeader.TryParse(astcFileBytes, 0, out uint dimX, out uint dimY, out uint dimZ, out int width, out int height, out int depth))
            {
                Debug.LogError("Invalid .astc file header.");
                return null;
            }

            if (depth != 1 || !AstcHeader.TryMapBlockSize(dimX, dimY, dimZ, out BlockSize blockSize))
            {
                Debug.LogError($"Unsupported .astc block or texture dimensions: block={dimX}x{dimY}x{dimZ}, size={width}x{height}x{depth}.");
                return null;
            }

            int payloadLen = astcFileBytes.Length - AstcHeader.HeaderSize;
            if (payloadLen <= 0)
            {
                Debug.LogError(".astc file contains no payload data.");
                return null;
            }

            fixed (byte* ptr = &astcFileBytes[AstcHeader.HeaderSize])
            {
                return DecompressToTexture((IntPtr)ptr, payloadLen, width, height, blockSize, srgb, isHdr, threadCount, makeNoLongerReadable);
            }
        }

        internal static unsafe AstcRaw CompressRawPixels(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            bool useUnityJobSystem = false)
        {
            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (pixelPtr == IntPtr.Zero || width <= 0 || height <= 0)
            {
                Debug.LogError("Invalid raw pixel buffer or dimensions.");
                return null;
            }

            int bpp = GetSourcePixelByteSize(sourceFormat);
            if (bpp <= 0 || !IsSupportGraphicFormat(sourceFormat))
            {
                Debug.LogError($"Unsupported GraphicsFormat for raw pixel ASTC compression: {sourceFormat}");
                return null;
            }

            long requiredBytes = (long)width * height * bpp;
            if (pixelByteLength < requiredBytes)
            {
                Debug.LogError($"Raw pixel buffer ({pixelByteLength} bytes) is smaller than required ({requiredBytes} bytes) for {width}x{height} {sourceFormat}.");
                return null;
            }

            using AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(sourceFormat, textureType, blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, useUnityJobSystem);
            if (lease.Context == null)
                return null;

            var snapshot = new CompactTextureSnapshot
            {
                Buffer = pixelPtr,
                Format = sourceFormat,
                Width = width,
                Height = height,
                MipCount = 1,
            };

            astcenc_swizzle swizzle = GetSwizzle(sourceFormat, textureType);
            return CompressCompactSnapshot(lease.Context, swizzle, snapshot, CancellationToken.None);
        }

        internal static unsafe Task<AstcRaw> CompressRawPixelsAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<AstcRaw>(cancellationToken);

            if (!IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return Task.FromResult<AstcRaw>(null);
            }

            if (pixelPtr == IntPtr.Zero || width <= 0 || height <= 0)
            {
                Debug.LogError("Invalid raw pixel buffer or dimensions.");
                return Task.FromResult<AstcRaw>(null);
            }

            int bpp = GetSourcePixelByteSize(sourceFormat);
            if (bpp <= 0 || !IsSupportGraphicFormat(sourceFormat))
            {
                Debug.LogError($"Unsupported GraphicsFormat for raw pixel ASTC compression: {sourceFormat}");
                return Task.FromResult<AstcRaw>(null);
            }

            long requiredBytes = (long)width * height * bpp;
            if (requiredBytes <= 0 || requiredBytes > int.MaxValue || pixelByteLength < requiredBytes)
            {
                Debug.LogError($"Raw pixel buffer ({pixelByteLength} bytes) is invalid or smaller than required ({requiredBytes} bytes) for {width}x{height} {sourceFormat}.");
                return Task.FromResult<AstcRaw>(null);
            }

            // Copy caller-owned pixels once so the background task is safe even if the caller frees/reuses its buffer.
            IntPtr copy = Marshal.AllocHGlobal((int)requiredBytes);
            try
            {
                UnsafeUtility.MemCpy((void*)copy, (void*)pixelPtr, requiredBytes);
            }
            catch
            {
                Marshal.FreeHGlobal(copy);
                throw;
            }

            var snapshot = new CompactTextureSnapshot
            {
                Buffer = copy,
                Format = sourceFormat,
                Width = width,
                Height = height,
                MipCount = 1,
            };

            return RunCpuTask(() =>
            {
                using (snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(sourceFormat, textureType, blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius);
                    if (lease.Context == null)
                        return null;
                    astcenc_swizzle swizzle = GetSwizzle(sourceFormat, textureType);
                    return CompressCompactSnapshot(lease.Context, swizzle, snapshot, cancellationToken);
                }
            }, cancellationToken);
        }

        internal static unsafe bool CompressRawPixelsInto(
            IntPtr pixelPtr,
            int pixelByteLength,
            IntPtr destPtr,
            int destCapacity,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            bool useUnityJobSystem,
            out int bytesWritten)
        {
            bytesWritten = 0;
            if (!IsSupported || pixelPtr == IntPtr.Zero || destPtr == IntPtr.Zero || width <= 0 || height <= 0)
                return false;

            int bpp = GetSourcePixelByteSize(sourceFormat);
            if (bpp <= 0 || !IsSupportGraphicFormat(sourceFormat))
                return false;

            long requiredSrcBytes = (long)width * height * bpp;
            if (requiredSrcBytes <= 0 || pixelByteLength < requiredSrcBytes)
                return false;

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return false;

            long expectedOutBytes = ((width + (long)bx - 1L) / (long)bx) * ((height + (long)by - 1L) / (long)by) * k_BytePerBlock;
            if (expectedOutBytes <= 0 || expectedOutBytes > destCapacity)
                return false;

            using AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(sourceFormat, textureType, blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, useUnityJobSystem);
            if (lease.Context == null)
                return false;

            astcenc_swizzle swizzle = GetSwizzle(sourceFormat, textureType);
            int bitness = GetBitness(sourceFormat);
            bool direct4Channel = IsDirect4ChannelFormat(sourceFormat);

            if (!direct4Channel)
            {
                // Reuse the already-leased context directly without allocating a second AstcContext.
                var snapshot = new CompactTextureSnapshot
                {
                    Buffer = pixelPtr,
                    Format = sourceFormat,
                    Width = width,
                    Height = height,
                    MipCount = 1,
                };
                using AstcRaw tmp = CompressCompactSnapshot(lease.Context, swizzle, snapshot, CancellationToken.None);
                if (tmp == null || tmp.Length > destCapacity)
                    return false;
                UnsafeUtility.MemCpy((void*)destPtr, (void*)tmp.Data, tmp.Length);
                bytesWritten = tmp.Length;
                return true;
            }

            astcenc_image image = new astcenc_image();
            try
            {
                if (bitness == 8) image.LoadFromDirectPtr((RGBA8*)pixelPtr, width, height, 8);
                else if (bitness == 16) image.LoadFromDirectPtr((RGBA16F*)pixelPtr, width, height, 16);
                else image.LoadFromDirectPtr((RGBA32F*)pixelPtr, width, height, 32);

                astcenc_error status = lease.Context.CompressSingleImage(ref image, ref swizzle, destPtr, (int)expectedOutBytes, CancellationToken.None);
                if (status != astcenc_error.ASTCENC_SUCCESS)
                    return false;

                bytesWritten = (int)expectedOutBytes;
                return true;
            }
            finally
            {
                image.Release(ownsSliceMemory: false);
            }
        }

        private static bool IsDirect4ChannelFormat(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.R16G16B16A16_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return true;
                default:
                    return false;
            }
        }

        private static int GetBitness(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R16G16_SFloat:
                case GraphicsFormat.R16G16B16_SFloat:
                case GraphicsFormat.R16G16B16A16_SFloat:
                    return 16;
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16G16_UNorm:
                case GraphicsFormat.R16G16B16_UNorm:
                case GraphicsFormat.R16G16B16A16_UNorm:
                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.R32G32_SFloat:
                case GraphicsFormat.R32G32B32_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return 32;
                default:
                    return 8;
            }
        }
    }
}
