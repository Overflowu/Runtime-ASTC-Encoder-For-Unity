using System;
using System.Collections.Generic;
using Aperture.Astcenc.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;

namespace Aperture.Astcenc.Tests.EditMode
{
    public class AstcEncoderEditModeTests
    {
        private readonly List<UnityEngine.Object> m_CreatedObjects = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object obj in m_CreatedObjects)
            {
                if (obj != null)
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }
            m_CreatedObjects.Clear();
        }

        private Texture2D CreateTexture(int width, int height, TextureFormat format, bool mipChain = false, bool linear = false)
        {
            Texture2D tex = new Texture2D(width, height, format, mipChain, linear);
            m_CreatedObjects.Add(tex);

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] = new Color(
                        (float)x / Math.Max(1, width - 1),
                        (float)y / Math.Max(1, height - 1),
                        ((x + y) & 0xFF) / 255.0f,
                        0.8f);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(mipChain, false);
            return tex;
        }

        private static int ExpectedCompressedBytes(int width, int height, int mipCount, int blockDim)
        {
            int total = 0;
            for (int i = 0; i < mipCount; i++)
            {
                int mw = Math.Max(1, width >> i);
                int mh = Math.Max(1, height >> i);
                int bx = (mw + blockDim - 1) / blockDim;
                int by = (mh + blockDim - 1) / blockDim;
                total += bx * by * 16;
            }
            return total;
        }

        [Test]
        public void IsSupport_HandlesNullReadableAndNonReadableTextures()
        {
            Assert.IsFalse(Encoder.IsSupport(null));

            Texture2D tex = CreateTexture(16, 16, TextureFormat.RGBA32);
            Assert.IsTrue(Encoder.IsSupport(tex));

            tex.Apply(false, makeNoLongerReadable: true);
            Assert.IsFalse(Encoder.IsSupport(tex));

            LogAssert.Expect(LogType.Error, "Texture format is not support or read/write is disabled.");
            Assert.IsNull(tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest));
        }

        [TestCase(BlockSize.ASTC_4x4, 4)]
        [TestCase(BlockSize.ASTC_5x5, 5)]
        [TestCase(BlockSize.ASTC_6x6, 6)]
        [TestCase(BlockSize.ASTC_8x8, 8)]
        [TestCase(BlockSize.ASTC_10x10, 10)]
        [TestCase(BlockSize.ASTC_12x12, 12)]
        public void CompressTexture_AllBlockSizes_ReturnsExactByteLengthAndDisposesCleanly(BlockSize blockSize, int blockDim)
        {
            Texture2D tex = CreateTexture(32, 32, TextureFormat.RGBA32, mipChain: false);
            int expectedBytes = ExpectedCompressedBytes(32, 32, 1, blockDim);

            AstcRaw raw = tex.CompressTexture(TextureType.Default, blockSize, CompressQuality.Fastest);
            Assert.IsNotNull(raw);
            Assert.AreEqual(expectedBytes, raw.Length);
            Assert.DoesNotThrow(() => raw.ThrowIfDisposed());

            raw.Dispose();
            Assert.Throws<ObjectDisposedException>(() => raw.ThrowIfDisposed());
        }

        [Test]
        public void EncodeToASTC_NonPowerOfTwo_WritesValidHeaderAndPayload()
        {
            const int width = 37;
            const int height = 23;
            Texture2D tex = CreateTexture(width, height, TextureFormat.RGBA32, mipChain: false);

            byte[] data = tex.EncodeToASTC(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast);
            Assert.IsNotNull(data);

            int expectedPayload = ExpectedCompressedBytes(width, height, 1, 6);
            Assert.AreEqual(16 + expectedPayload, data.Length);

            // ASTC magic constant 0x5CA1AB13 (little-endian)
            Assert.AreEqual(0x13, data[0]);
            Assert.AreEqual(0xAB, data[1]);
            Assert.AreEqual(0xA1, data[2]);
            Assert.AreEqual(0x5C, data[3]);

            // Block dimensions
            Assert.AreEqual(6, data[4]);
            Assert.AreEqual(6, data[5]);
            Assert.AreEqual(1, data[6]);

            // 24-bit little-endian image dimensions
            int headerW = data[7] | (data[8] << 8) | (data[9] << 16);
            int headerH = data[10] | (data[11] << 8) | (data[12] << 16);
            int headerZ = data[13] | (data[14] << 8) | (data[15] << 16);
            Assert.AreEqual(width, headerW);
            Assert.AreEqual(height, headerH);
            Assert.AreEqual(1, headerZ);
        }

        [Test]
        public void CompressTexture_NonSquareWithMipmaps_CompressesFullMipChain()
        {
            Texture2D tex = CreateTexture(64, 32, TextureFormat.RGBA32, mipChain: true);
            Assert.Greater(tex.mipmapCount, 1);

            int expectedBytes = ExpectedCompressedBytes(64, 32, tex.mipmapCount, 6);
            using (AstcRaw raw = tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest))
            {
                Assert.IsNotNull(raw);
                Assert.AreEqual(expectedBytes, raw.Length);
            }
        }

        [TestCase(TextureFormat.RGBA32, TextureType.Default, false)]
        [TestCase(TextureFormat.RGBA32, TextureType.Default, true)]
        [TestCase(TextureFormat.RGBA32, TextureType.Normal, true)]
        [TestCase(TextureFormat.RGB24, TextureType.Default, false)]
        [TestCase(TextureFormat.R8, TextureType.Default, true)]
        [TestCase(TextureFormat.RG16, TextureType.Default, true)]
        [TestCase(TextureFormat.RGBAHalf, TextureType.Default, true)]
        [TestCase(TextureFormat.RGBAFloat, TextureType.Default, true)]
        public void CompressTexture_SupportedInputFormatsAndNormalMap_Succeeds(TextureFormat format, TextureType textureType, bool linear)
        {
            Texture2D tex = CreateTexture(16, 16, format, mipChain: false, linear: linear);
            Assert.IsTrue(Encoder.IsSupport(tex), $"Format {format} ({tex.graphicsFormat}) should be supported.");

            int expectedBytes = ExpectedCompressedBytes(16, 16, 1, 4);
            using (AstcRaw raw = tex.CompressTexture(textureType, BlockSize.ASTC_4x4, CompressQuality.Fastest))
            {
                Assert.IsNotNull(raw);
                Assert.AreEqual(expectedBytes, raw.Length);
            }
        }

        [TestCase(CompressQuality.Fastest)]
        [TestCase(CompressQuality.Fast)]
        [TestCase(CompressQuality.Medium)]
        public void CompressTexture_QualityPresets_Succeed(CompressQuality quality)
        {
            Texture2D tex = CreateTexture(16, 16, TextureFormat.RGBA32, mipChain: false);
            int expectedBytes = ExpectedCompressedBytes(16, 16, 1, 6);

            using (AstcRaw raw = tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, quality))
            {
                Assert.IsNotNull(raw);
                Assert.AreEqual(expectedBytes, raw.Length);
            }
        }

        [Test]
        public void CompressTexture_SampleResourceTextures_SucceedsIfReadable()
        {
            Texture2D color = Resources.Load<Texture2D>("Color");
            if (color != null && Encoder.IsSupport(color))
            {
                using (AstcRaw raw = color.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest))
                {
                    Assert.IsNotNull(raw);
                    Assert.AreEqual(ExpectedCompressedBytes(color.width, color.height, color.mipmapCount, 6), raw.Length);
                }
            }

            Texture2D normal = Resources.Load<Texture2D>("Normal");
            if (normal != null && Encoder.IsSupport(normal))
            {
                using (AstcRaw raw = normal.CompressTexture(TextureType.Normal, BlockSize.ASTC_6x6, CompressQuality.Fastest))
                {
                    Assert.IsNotNull(raw);
                    Assert.AreEqual(ExpectedCompressedBytes(normal.width, normal.height, normal.mipmapCount, 6), raw.Length);
                }
            }
        }

        [Test]
        public void Compress_InPlace_ReinitializesToAstcWhenGraphicsDeviceSupportsAstc()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.RGBA_ASTC6X6_UNorm, FormatUsage.Sample))
            {
                Assert.Ignore($"ASTC GPU texture sampling is not supported on current graphics device ({SystemInfo.graphicsDeviceType}).");
                return;
            }

            Texture2D tex = CreateTexture(32, 32, TextureFormat.RGBA32, mipChain: false, linear: true);
            bool ok = tex.Compress(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest, makeNoLongerReadable: true);

            Assert.IsTrue(ok);
            Assert.AreEqual(GraphicsFormat.RGBA_ASTC6X6_UNorm, tex.graphicsFormat);
            Assert.AreEqual(32, tex.width);
            Assert.AreEqual(32, tex.height);
            Assert.AreEqual(1, tex.mipmapCount);
            Assert.IsFalse(tex.isReadable);
        }
    }
}
