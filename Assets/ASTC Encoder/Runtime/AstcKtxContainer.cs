using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Parsed metadata and raw ASTC payload extracted from a standard Khronos KTX v1 container.
    /// </summary>
    public readonly struct KtxAstcData
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int MipCount;
        public readonly BlockSize BlockSize;
        public readonly GraphicsFormat GraphicsFormat;
        public readonly byte[] RawAstcPayload;

        public KtxAstcData(int width, int height, int mipCount, BlockSize blockSize, GraphicsFormat graphicsFormat, byte[] rawAstcPayload)
        {
            Width = width;
            Height = height;
            MipCount = mipCount;
            BlockSize = blockSize;
            GraphicsFormat = graphicsFormat;
            RawAstcPayload = rawAstcPayload;
        }
    }

    /// <summary>
    /// Zero-dependency Khronos KTX v1 container encoder and decoder for runtime ASTC textures.
    /// Supports full mipmap chains and preserves exact GraphicsFormat (LDR UNorm, LDR sRGB, and HDR UFloat).
    /// </summary>
    public static class AstcKtxContainer
    {
        private const int k_HeaderSize = 64;
        private const int k_BytesPerBlock = 16;
        private const uint k_Endianness = 0x04030201u;
        private const uint k_GlRgba = 0x1908u;

        // Khronos GL_KHR_texture_compression_astc_ldr / hdr internal formats
        private const uint k_GlAstc4x4Rgba   = 0x93B0u;
        private const uint k_GlAstc5x5Rgba   = 0x93B2u;
        private const uint k_GlAstc6x6Rgba   = 0x93B4u;
        private const uint k_GlAstc8x8Rgba   = 0x93B7u;
        private const uint k_GlAstc10x10Rgba = 0x93BBu;
        private const uint k_GlAstc12x12Rgba = 0x93BDu;

        private const uint k_GlAstc4x4Srgb   = 0x93D0u;
        private const uint k_GlAstc5x5Srgb   = 0x93D2u;
        private const uint k_GlAstc6x6Srgb   = 0x93D4u;
        private const uint k_GlAstc8x8Srgb   = 0x93D7u;
        private const uint k_GlAstc10x10Srgb = 0x93DBu;
        private const uint k_GlAstc12x12Srgb = 0x93DDu;

        private static readonly byte[] s_KtxIdentifier =
        {
            0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
        };

        private static readonly byte[] s_HdrKvEntry = Encoding.ASCII.GetBytes("ASTC_PROFILE\0HDR\0");

        /// <summary>
        /// Encodes contiguous raw ASTC mipmap data (<see cref="AstcRaw"/>) into a standard Khronos KTX v1 byte array.
        /// </summary>
        public static byte[] Encode(AstcRaw raw, int width, int height, int mipCount, GraphicsFormat astcFormat)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));
            raw.ThrowIfDisposed();

            if (width <= 0 || height <= 0 || mipCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Width, height, and mipCount must be positive.");

            if (!TryMapGraphicsFormatToGl(astcFormat, out uint glInternalFormat, out BlockSize blockSize, out bool isHdr))
                throw new ArgumentException($"Unsupported ASTC GraphicsFormat for KTX v1: {astcFormat}", nameof(astcFormat));

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                throw new ArgumentException($"Unsupported ASTC block size: {blockSize}");

            int expectedPayloadBytes = 0;
            for (int mip = 0; mip < mipCount; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int blocksX = (mipW + (int)bx - 1) / (int)bx;
                int blocksY = (mipH + (int)by - 1) / (int)by;
                expectedPayloadBytes += blocksX * blocksY * k_BytesPerBlock;
            }

            if (raw.Length < expectedPayloadBytes)
                throw new ArgumentException($"AstcRaw length ({raw.Length}) is smaller than required ({expectedPayloadBytes}) for {width}x{height} ({mipCount} mips).");

            int kvBytes = 0;
            if (isHdr)
            {
                int padded = (s_HdrKvEntry.Length + 3) & ~3;
                kvBytes = 4 + padded;
            }

            int totalSize = k_HeaderSize + kvBytes + mipCount * 4 + expectedPayloadBytes;
            byte[] output = new byte[totalSize];

            using (var ms = new MemoryStream(output, writable: true))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(s_KtxIdentifier);
                bw.Write(k_Endianness);
                bw.Write(0u); // glType = 0 (compressed)
                bw.Write(1u); // glTypeSize = 1
                bw.Write(0u); // glFormat = 0 (compressed)
                bw.Write(glInternalFormat);
                bw.Write(k_GlRgba);
                bw.Write((uint)width);
                bw.Write((uint)height);
                bw.Write(0u); // pixelDepth (2D)
                bw.Write(0u); // numberOfArrayElements
                bw.Write(1u); // numberOfFaces
                bw.Write((uint)mipCount);
                bw.Write((uint)kvBytes);

                if (isHdr)
                {
                    bw.Write((uint)s_HdrKvEntry.Length);
                    bw.Write(s_HdrKvEntry);
                    int pad = ((s_HdrKvEntry.Length + 3) & ~3) - s_HdrKvEntry.Length;
                    for (int p = 0; p < pad; p++) bw.Write((byte)0);
                }

                int srcOffset = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int mipW = Math.Max(1, width >> mip);
                    int mipH = Math.Max(1, height >> mip);
                    int blocksX = (mipW + (int)bx - 1) / (int)bx;
                    int blocksY = (mipH + (int)by - 1) / (int)by;
                    int mipBytes = blocksX * blocksY * k_BytesPerBlock;

                    bw.Write((uint)mipBytes);
                    int dstOffset = (int)ms.Position;
                    Marshal.Copy(raw.Data + srcOffset, output, dstOffset, mipBytes);
                    ms.Position = dstOffset + mipBytes;
                    srcOffset += mipBytes;
                }
            }

            return output;
        }

        /// <summary>
        /// Parses a Khronos KTX v1 byte array into contiguous ASTC mipmap data suitable for Unity's <see cref="Texture2D.LoadRawTextureData(byte[])"/>.
        /// </summary>
        public static bool TryDecode(byte[] ktxBytes, out KtxAstcData data)
        {
            data = default;
            if (ktxBytes == null || ktxBytes.Length < k_HeaderSize)
                return false;

            for (int i = 0; i < s_KtxIdentifier.Length; i++)
            {
                if (ktxBytes[i] != s_KtxIdentifier[i])
                    return false;
            }

            using (var ms = new MemoryStream(ktxBytes, writable: false))
            using (var br = new BinaryReader(ms))
            {
                ms.Position = 12;
                uint endianness = br.ReadUInt32();
                if (endianness != k_Endianness)
                    return false;

                uint glType = br.ReadUInt32();
                br.ReadUInt32(); // glTypeSize
                uint glFormat = br.ReadUInt32();
                uint glInternalFormat = br.ReadUInt32();
                br.ReadUInt32(); // glBaseInternalFormat
                uint pixelWidth = br.ReadUInt32();
                uint pixelHeight = br.ReadUInt32();
                uint pixelDepth = br.ReadUInt32();
                uint arrayElements = br.ReadUInt32();
                uint faces = br.ReadUInt32();
                uint mipLevels = br.ReadUInt32();
                uint kvBytes = br.ReadUInt32();

                if (glType != 0 || glFormat != 0 || pixelWidth == 0 || pixelHeight == 0 || pixelWidth > 16384u || pixelHeight > 16384u || pixelDepth > 1 || arrayElements > 1 || faces != 1)
                    return false;

                int maxMips = AstcGpuBackend.CalculateFullMipCount((int)pixelWidth, (int)pixelHeight);
                if (mipLevels > (uint)maxMips)
                    return false;

                int mipCount = (int)Math.Max(1u, mipLevels);
                if (k_HeaderSize + (long)kvBytes > ktxBytes.Length)
                    return false;

                bool isHdr = false;
                if (kvBytes > 0)
                {
                    long kvEnd = k_HeaderSize + (long)kvBytes;
                    while (ms.Position + 4 <= kvEnd)
                    {
                        uint entryLen = br.ReadUInt32();
                        if (ms.Position + (long)entryLen > kvEnd)
                            break;

                        if (entryLen == (uint)s_HdrKvEntry.Length)
                        {
                            byte[] entry = br.ReadBytes((int)entryLen);
                            bool match = true;
                            for (int i = 0; i < s_HdrKvEntry.Length; i++)
                            {
                                if (entry[i] != s_HdrKvEntry[i])
                                {
                                    match = false;
                                    break;
                                }
                            }
                            if (match) isHdr = true;
                        }
                        else
                        {
                            ms.Position += entryLen;
                        }

                        long rem = (4 - (entryLen & 3)) & 3;
                        ms.Position = Math.Min(kvEnd, ms.Position + rem);
                    }
                    ms.Position = kvEnd;
                }

                if (!TryMapGlToGraphicsFormat(glInternalFormat, isHdr, out BlockSize blockSize, out GraphicsFormat graphicsFormat))
                    return false;

                if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                    return false;

                long totalPayloadBytes = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int mipW = Math.Max(1, (int)pixelWidth >> mip);
                    int mipH = Math.Max(1, (int)pixelHeight >> mip);
                    long blocksX = (mipW + (long)bx - 1L) / (long)bx;
                    long blocksY = (mipH + (long)by - 1L) / (long)by;
                    totalPayloadBytes += blocksX * blocksY * k_BytesPerBlock;
                }

                if (totalPayloadBytes <= 0 || totalPayloadBytes > ktxBytes.Length - ms.Position)
                    return false;

                byte[] payload = new byte[(int)totalPayloadBytes];
                int dstOffset = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    if (ms.Position + 4 > ktxBytes.Length)
                        return false;

                    uint imageSize = br.ReadUInt32();
                    int mipW = Math.Max(1, (int)pixelWidth >> mip);
                    int mipH = Math.Max(1, (int)pixelHeight >> mip);
                    int expectedMipBytes = ((mipW + (int)bx - 1) / (int)bx) * ((mipH + (int)by - 1) / (int)by) * k_BytesPerBlock;

                    if (imageSize < (uint)expectedMipBytes || ms.Position + (long)imageSize > ktxBytes.Length)
                        return false;

                    Buffer.BlockCopy(ktxBytes, (int)ms.Position, payload, dstOffset, expectedMipBytes);
                    dstOffset += expectedMipBytes;
                    ms.Position += imageSize;

                    long pad = (4 - (imageSize & 3)) & 3;
                    if (ms.Position + pad <= ktxBytes.Length)
                        ms.Position += pad;
                }

                data = new KtxAstcData((int)pixelWidth, (int)pixelHeight, mipCount, blockSize, graphicsFormat, payload);
                return true;
            }
        }

        /// <summary>
        /// Decodes a KTX v1 byte array directly into a GPU-ready ASTC <see cref="Texture2D"/> (with full mipmaps).
        /// On devices without hardware ASTC sampling, falls back to CPU software decompression of mip 0 when available.
        /// </summary>
        public static Texture2D LoadTexture(byte[] ktxBytes, bool makeNoLongerReadable = true, string textureName = null)
        {
            if (!TryDecode(ktxBytes, out KtxAstcData data))
            {
                Debug.LogError("Failed to decode KTX v1 ASTC container.");
                return null;
            }

            if (Encoder.CanCreateAstcTexture(data.GraphicsFormat))
            {
                TextureCreationFlags flags = data.MipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
                var tex = new Texture2D(data.Width, data.Height, data.GraphicsFormat, data.MipCount, flags);
                try
                {
                    if (!string.IsNullOrEmpty(textureName))
                        tex.name = textureName;
                    tex.LoadRawTextureData(data.RawAstcPayload);
                    tex.Apply(false, makeNoLongerReadable);
                    return tex;
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    throw;
                }
            }

            // Fallback on non-ASTC platforms: software-decompress mip 0 via native CPU library.
            if (!Encoder.GetBlockSize(data.BlockSize, out uint bx, out uint by, out _))
                return null;

            int mip0Bytes = ((data.Width + (int)bx - 1) / (int)bx) * ((data.Height + (int)by - 1) / (int)by) * k_BytesPerBlock;
            if (mip0Bytes > data.RawAstcPayload.Length)
                return null;

            bool srgb = GraphicsFormatUtility.IsSRGBFormat(data.GraphicsFormat);
            bool isHdr = GraphicsFormatUtility.IsFloatFormat(data.GraphicsFormat);
            byte[] rgba = AstcCpuBackend.DecompressToBytes(data.RawAstcPayload, 0, mip0Bytes, data.Width, data.Height, data.BlockSize, srgb, isHdr, isHdr ? 16 : 8);
            if (rgba == null)
                return null;

            GraphicsFormat dstFmt = isHdr
                ? GraphicsFormat.R16G16B16A16_SFloat
                : (srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm);
            var fallback = new Texture2D(data.Width, data.Height, dstFmt, TextureCreationFlags.None);
            try
            {
                if (!string.IsNullOrEmpty(textureName))
                    fallback.name = textureName;
                fallback.LoadRawTextureData(rgba);
                fallback.Apply(false, makeNoLongerReadable);
                return fallback;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(fallback);
                throw;
            }
        }

        internal static int InferMipCount(int payloadBytes, int width, int height, BlockSize blockSize)
        {
            if (payloadBytes <= 0 || width <= 0 || height <= 0 || !Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return 0;

            int maxMips = AstcGpuBackend.CalculateFullMipCount(width, height);
            int total = 0;
            for (int mip = 0; mip < maxMips; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int blocksX = (mipW + (int)bx - 1) / (int)bx;
                int blocksY = (mipH + (int)by - 1) / (int)by;
                total += blocksX * blocksY * k_BytesPerBlock;
                if (total == payloadBytes)
                    return mip + 1;
                if (total > payloadBytes)
                    return mip;
            }
            return maxMips;
        }

        private static bool TryMapGraphicsFormatToGl(GraphicsFormat format, out uint glInternal, out BlockSize blockSize, out bool isHdr)
        {
            isHdr = false;
            switch (format)
            {
                case GraphicsFormat.RGBA_ASTC4X4_UNorm:   glInternal = k_GlAstc4x4Rgba;   blockSize = BlockSize.ASTC_4x4;   return true;
                case GraphicsFormat.RGBA_ASTC5X5_UNorm:   glInternal = k_GlAstc5x5Rgba;   blockSize = BlockSize.ASTC_5x5;   return true;
                case GraphicsFormat.RGBA_ASTC6X6_UNorm:   glInternal = k_GlAstc6x6Rgba;   blockSize = BlockSize.ASTC_6x6;   return true;
                case GraphicsFormat.RGBA_ASTC8X8_UNorm:   glInternal = k_GlAstc8x8Rgba;   blockSize = BlockSize.ASTC_8x8;   return true;
                case GraphicsFormat.RGBA_ASTC10X10_UNorm: glInternal = k_GlAstc10x10Rgba; blockSize = BlockSize.ASTC_10x10; return true;
                case GraphicsFormat.RGBA_ASTC12X12_UNorm: glInternal = k_GlAstc12x12Rgba; blockSize = BlockSize.ASTC_12x12; return true;

                case GraphicsFormat.RGBA_ASTC4X4_SRGB:    glInternal = k_GlAstc4x4Srgb;   blockSize = BlockSize.ASTC_4x4;   return true;
                case GraphicsFormat.RGBA_ASTC5X5_SRGB:    glInternal = k_GlAstc5x5Srgb;   blockSize = BlockSize.ASTC_5x5;   return true;
                case GraphicsFormat.RGBA_ASTC6X6_SRGB:    glInternal = k_GlAstc6x6Srgb;   blockSize = BlockSize.ASTC_6x6;   return true;
                case GraphicsFormat.RGBA_ASTC8X8_SRGB:    glInternal = k_GlAstc8x8Srgb;   blockSize = BlockSize.ASTC_8x8;   return true;
                case GraphicsFormat.RGBA_ASTC10X10_SRGB:  glInternal = k_GlAstc10x10Srgb; blockSize = BlockSize.ASTC_10x10; return true;
                case GraphicsFormat.RGBA_ASTC12X12_SRGB:  glInternal = k_GlAstc12x12Srgb; blockSize = BlockSize.ASTC_12x12; return true;

                case GraphicsFormat.RGBA_ASTC4X4_UFloat:   glInternal = k_GlAstc4x4Rgba;   blockSize = BlockSize.ASTC_4x4;   isHdr = true; return true;
                case GraphicsFormat.RGBA_ASTC5X5_UFloat:   glInternal = k_GlAstc5x5Rgba;   blockSize = BlockSize.ASTC_5x5;   isHdr = true; return true;
                case GraphicsFormat.RGBA_ASTC6X6_UFloat:   glInternal = k_GlAstc6x6Rgba;   blockSize = BlockSize.ASTC_6x6;   isHdr = true; return true;
                case GraphicsFormat.RGBA_ASTC8X8_UFloat:   glInternal = k_GlAstc8x8Rgba;   blockSize = BlockSize.ASTC_8x8;   isHdr = true; return true;
                case GraphicsFormat.RGBA_ASTC10X10_UFloat: glInternal = k_GlAstc10x10Rgba; blockSize = BlockSize.ASTC_10x10; isHdr = true; return true;
                case GraphicsFormat.RGBA_ASTC12X12_UFloat: glInternal = k_GlAstc12x12Rgba; blockSize = BlockSize.ASTC_12x12; isHdr = true; return true;

                default:
                    glInternal = 0;
                    blockSize = BlockSize.ASTC_None;
                    return false;
            }
        }

        private static bool TryMapGlToGraphicsFormat(uint glInternal, bool isHdr, out BlockSize blockSize, out GraphicsFormat format)
        {
            switch (glInternal)
            {
                case k_GlAstc4x4Rgba:   blockSize = BlockSize.ASTC_4x4;   format = isHdr ? GraphicsFormat.RGBA_ASTC4X4_UFloat   : GraphicsFormat.RGBA_ASTC4X4_UNorm;   return true;
                case k_GlAstc5x5Rgba:   blockSize = BlockSize.ASTC_5x5;   format = isHdr ? GraphicsFormat.RGBA_ASTC5X5_UFloat   : GraphicsFormat.RGBA_ASTC5X5_UNorm;   return true;
                case k_GlAstc6x6Rgba:   blockSize = BlockSize.ASTC_6x6;   format = isHdr ? GraphicsFormat.RGBA_ASTC6X6_UFloat   : GraphicsFormat.RGBA_ASTC6X6_UNorm;   return true;
                case k_GlAstc8x8Rgba:   blockSize = BlockSize.ASTC_8x8;   format = isHdr ? GraphicsFormat.RGBA_ASTC8X8_UFloat   : GraphicsFormat.RGBA_ASTC8X8_UNorm;   return true;
                case k_GlAstc10x10Rgba: blockSize = BlockSize.ASTC_10x10; format = isHdr ? GraphicsFormat.RGBA_ASTC10X10_UFloat : GraphicsFormat.RGBA_ASTC10X10_UNorm; return true;
                case k_GlAstc12x12Rgba: blockSize = BlockSize.ASTC_12x12; format = isHdr ? GraphicsFormat.RGBA_ASTC12X12_UFloat : GraphicsFormat.RGBA_ASTC12X12_UNorm; return true;

                case k_GlAstc4x4Srgb:   blockSize = BlockSize.ASTC_4x4;   format = GraphicsFormat.RGBA_ASTC4X4_SRGB;   return true;
                case k_GlAstc5x5Srgb:   blockSize = BlockSize.ASTC_5x5;   format = GraphicsFormat.RGBA_ASTC5X5_SRGB;   return true;
                case k_GlAstc6x6Srgb:   blockSize = BlockSize.ASTC_6x6;   format = GraphicsFormat.RGBA_ASTC6X6_SRGB;   return true;
                case k_GlAstc8x8Srgb:   blockSize = BlockSize.ASTC_8x8;   format = GraphicsFormat.RGBA_ASTC8X8_SRGB;   return true;
                case k_GlAstc10x10Srgb: blockSize = BlockSize.ASTC_10x10; format = GraphicsFormat.RGBA_ASTC10X10_SRGB; return true;
                case k_GlAstc12x12Srgb: blockSize = BlockSize.ASTC_12x12; format = GraphicsFormat.RGBA_ASTC12X12_SRGB; return true;

                default:
                    blockSize = BlockSize.ASTC_None;
                    format = GraphicsFormat.None;
                    return false;
            }
        }
    }
}
