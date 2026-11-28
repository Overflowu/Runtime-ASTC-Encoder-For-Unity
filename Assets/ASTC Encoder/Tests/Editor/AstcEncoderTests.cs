using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Tests
{
    /// <summary>
    /// Golden EditMode verification suite covering header/KTX containers, disk caching,
    /// raw pixel encoding, queue scheduling, and CPU/GPU cross-decode PSNR & normal-map accuracy.
    /// </summary>
    public class AstcEncoderTests
    {
        private static readonly BlockSize[] s_AllBlockSizes =
        {
            BlockSize.ASTC_4x4,
            BlockSize.ASTC_5x5,
            BlockSize.ASTC_6x6,
            BlockSize.ASTC_8x8,
            BlockSize.ASTC_10x10,
            BlockSize.ASTC_12x12
        };

        [Test]
        public void AstcHeader_RoundTrip_AllBlockSizes_AndRejectsInvalid()
        {
            byte[] buf = new byte[AstcHeader.HeaderSize];
            uint[] dims = { 4u, 5u, 6u, 8u, 10u, 12u };
            for (int i = 0; i < dims.Length; i++)
            {
                AstcHeader.WriteTo(buf, 0, dims[i], dims[i], 1u, 137, 211, 1);
                Assert.IsTrue(AstcHeader.TryParse(buf, 0, out uint bx, out uint by, out uint bz, out int w, out int h, out int d));
                Assert.AreEqual(dims[i], bx);
                Assert.AreEqual(dims[i], by);
                Assert.AreEqual(1u, bz);
                Assert.AreEqual(137, w);
                Assert.AreEqual(211, h);
                Assert.AreEqual(1, d);
                Assert.IsTrue(AstcHeader.TryMapBlockSize(bx, by, bz, out BlockSize mapped));
                Assert.AreEqual(s_AllBlockSizes[i], mapped);
            }

            // Corrupt magic must fail
            buf[0] = 0x00;
            Assert.IsFalse(AstcHeader.TryParse(buf, 0, out _, out _, out _, out _, out _, out _));
            Assert.IsFalse(AstcHeader.TryParse(new byte[8], 0, out _, out _, out _, out _, out _, out _));
        }

        [Test]
        public void KtxContainer_RoundTrip_LdrSrgbHdr_FullMipChain()
        {
            GraphicsFormat[] formats =
            {
                GraphicsFormat.RGBA_ASTC4X4_UNorm,
                GraphicsFormat.RGBA_ASTC6X6_SRGB,
                GraphicsFormat.RGBA_ASTC8X8_UFloat
            };
            int[] blockDims = { 4, 6, 8 };

            for (int f = 0; f < formats.Length; f++)
            {
                int width = 64;
                int height = 64;
                int mipCount = 7;
                int b = blockDims[f];
                int totalBytes = 0;
                for (int m = 0; m < mipCount; m++)
                {
                    int mw = Math.Max(1, width >> m);
                    int mh = Math.Max(1, height >> m);
                    totalBytes += ((mw + b - 1) / b) * ((mh + b - 1) / b) * 16;
                }

                byte[] payload = new byte[totalBytes];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)((i * 31 + f * 17) & 0xFF);

                IntPtr ptr = Marshal.AllocHGlobal(totalBytes);
                Marshal.Copy(payload, 0, ptr, totalBytes);
                using var raw = new AstcRaw(ptr, totalBytes);

                byte[] ktx = AstcKtxContainer.Encode(raw, width, height, mipCount, formats[f]);
                Assert.IsNotNull(ktx);
                Assert.IsTrue(AstcKtxContainer.TryDecode(ktx, out KtxAstcData decoded));
                Assert.AreEqual(width, decoded.Width);
                Assert.AreEqual(height, decoded.Height);
                Assert.AreEqual(mipCount, decoded.MipCount);
                Assert.AreEqual(formats[f], decoded.GraphicsFormat);
                Assert.AreEqual(payload.Length, decoded.RawAstcPayload.Length);
                CollectionAssert.AreEqual(payload, decoded.RawAstcPayload);
            }
        }

        [Test]
        public void KtxContainer_RejectsMalformedAndOverflowHeaders()
        {
            // Craft a 64-byte KTX header with huge mipLevels / dimensions to ensure TryDecode returns false cleanly without OOM or overflow.
            byte[] evil = new byte[64];
            byte[] magic = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
            Buffer.BlockCopy(magic, 0, evil, 0, magic.Length);
            BitConverter.GetBytes(0x04030201u).CopyTo(evil, 12); // endianness
            BitConverter.GetBytes(0x93B0u).CopyTo(evil, 28);     // GL_COMPRESSED_RGBA_ASTC_4x4_KHR
            BitConverter.GetBytes(0x40000000u).CopyTo(evil, 36); // absurd width
            BitConverter.GetBytes(0x40000000u).CopyTo(evil, 40); // absurd height
            BitConverter.GetBytes(1u).CopyTo(evil, 52);          // faces
            BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(evil, 56); // absurd mipLevels
            Assert.IsFalse(AstcKtxContainer.TryDecode(evil, out _));

            // Valid dimensions but absurd mipLevels
            BitConverter.GetBytes(64u).CopyTo(evil, 36);
            BitConverter.GetBytes(64u).CopyTo(evil, 40);
            Assert.IsFalse(AstcKtxContainer.TryDecode(evil, out _));

            // Valid dimensions & mipLevels = 1, but truncated payload
            BitConverter.GetBytes(1u).CopyTo(evil, 56);
            Assert.IsFalse(AstcKtxContainer.TryDecode(evil, out _));
        }

        [Test]
        public void CompressRawPixels_CpuCrossDecode_MeetsQualityTarget()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("Native CPU astcenc library is not available on this host.");

            const int width = 64;
            const int height = 64;
            byte[] pixels = CreateGradientRgba8(width, height);

            var options = AstcCompressOptions.Default;
            options.Backend = EncodeBackend.Cpu;
            options.BlockSize = BlockSize.ASTC_6x6;
            options.Quality = CompressQuality.Medium;

            using AstcRaw raw = Encoder.CompressRawPixels(pixels, width, height, GraphicsFormat.R8G8B8A8_UNorm, options);
            Assert.IsNotNull(raw);
            Assert.Greater(raw.Length, 0);

            byte[] decoded = Encoder.Decompress(raw, width, height, options.BlockSize, srgb: false, isHdr: false, outputBitness: 8);
            Assert.IsNotNull(decoded);
            Assert.AreEqual(pixels.Length, decoded.Length);

            double psnr = ComputeRgba8Psnr(pixels, decoded);
            Assert.Greater(psnr, 32.0, $"Expected CPU ASTC 6x6 gradient PSNR > 32 dB, got {psnr:F2} dB");
        }

        [Test]
        public void GpuCrossDecode_AllBlockSizes_AndNpot_ValidAstcBlocks()
        {
            if (!Encoder.IsGpuSupported || !Encoder.IsCpuSupported)
                Assert.Ignore("Both GPU compute and CPU astcenc decoder are required for cross-decode test.");

            int[] sizes = { 64, 53 };
            foreach (int dim in sizes)
            {
                Texture2D src = CreateTestTexture(dim, dim, normalMap: false);
                byte[] refBytes = CreateGradientRgba8(dim, dim);
                try
                {
                    foreach (BlockSize bs in s_AllBlockSizes)
                    {
                        var options = AstcCompressOptions.Default;
                        options.Backend = EncodeBackend.Gpu;
                        options.BlockSize = bs;
                        options.Quality = CompressQuality.Medium;
                        options.IncludeMipmaps = false;

                        using AstcRaw raw = src.CompressTexture(options);
                        Assert.IsNotNull(raw, $"GPU compress returned null for {dim}x{dim} {bs}");

                        byte[] decoded = Encoder.Decompress(raw, dim, dim, bs, srgb: false, isHdr: false, outputBitness: 8);
                        Assert.IsNotNull(decoded, $"CPU cross-decode returned null for {dim}x{dim} {bs}");

                        double psnr = ComputeRgba8Psnr(refBytes, decoded);
                        Assert.Greater(psnr, 24.0, $"GPU cross-decode PSNR too low for {dim}x{dim} {bs}: {psnr:F2} dB");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(src);
                }
            }
        }

        [Test]
        public void GpuCrossDecode_NormalMap_LowAngularError()
        {
            if (!Encoder.IsGpuSupported || !Encoder.IsCpuSupported)
                Assert.Ignore("Both GPU compute and CPU astcenc decoder are required for normal-map cross-decode test.");

            const int size = 64;
            Texture2D normalTex = CreateTestTexture(size, size, normalMap: true);
            Color[] refPixels = normalTex.GetPixels(0);
            try
            {
                var options = AstcCompressOptions.NormalMap;
                options.Backend = EncodeBackend.Gpu;
                options.BlockSize = BlockSize.ASTC_6x6;
                options.Quality = CompressQuality.Medium;
                options.IncludeMipmaps = false;

                using AstcRaw raw = normalTex.CompressTexture(options);
                Assert.IsNotNull(raw);

                byte[] decoded = Encoder.Decompress(raw, size, size, options.BlockSize, srgb: false, isHdr: false, outputBitness: 8);
                Assert.IsNotNull(decoded);

                double meanDeg = ComputeMeanNormalAngularErrorDeg(refPixels, decoded);
                Assert.Less(meanDeg, 3.0, $"Expected normal map mean angular error < 3.0°, got {meanDeg:F2}°");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(normalTex);
            }
        }

        [Test]
        public void AstcContext_ParentTableSharing_Succeeds()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("Native CPU astcenc library is not available on this host.");

            using AstcContext parent = AstcContext.Create(GraphicsFormat.R8G8B8A8_UNorm, TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast, threadCount: 1);
            Assert.IsNotNull(parent);

            using AstcContext child = AstcContext.Create(GraphicsFormat.R8G8B8A8_UNorm, TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast, threadCount: 1, parentContext: parent);
            Assert.IsNotNull(child);

            Texture2D src = CreateTestTexture(32, 32, normalMap: false);
            try
            {
                using AstcRaw raw = child.CompressTexture(src);
                Assert.IsNotNull(raw);
                Assert.Greater(raw.Length, 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void CompressionQueue_Cancellation_CancelsPendingTask()
        {
            using var queue = new AstcCompressionQueue();
            using var cts = new CancellationTokenSource();
            Texture2D src = CreateTestTexture(16, 16, normalMap: false);
            try
            {
                Task<AstcRaw> task = queue.EnqueueToRaw(src, AstcCompressOptions.Default, AstcTaskPriority.Low, cts.Token);
                cts.Cancel();
                queue.ProcessFrame();
                Assert.IsTrue(task.IsCanceled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(src);
            }
        }

        private static byte[] CreateGradientRgba8(int width, int height)
        {
            byte[] data = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    data[o + 0] = (byte)(x * 255 / Math.Max(1, width - 1));
                    data[o + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                    data[o + 2] = (byte)(((x + y) * 127) / Math.Max(1, width + height - 2));
                    data[o + 3] = 255;
                }
            }
            return data;
        }

        private static Texture2D CreateTestTexture(int width, int height, bool normalMap)
        {
            var tex = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
            var colors = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = width > 1 ? (float)x / (width - 1) : 0f;
                    float v = height > 1 ? (float)y / (height - 1) : 0f;
                    if (normalMap)
                    {
                        float nx = (u - 0.5f) * 1.2f;
                        float ny = (v - 0.5f) * 1.2f;
                        float nz = Mathf.Sqrt(Mathf.Max(0.01f, 1f - nx * nx - ny * ny));
                        Vector3 n = new Vector3(nx, ny, nz).normalized;
                        colors[y * width + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                    }
                    else
                    {
                        colors[y * width + x] = new Color(u, v, (u + v) * 0.5f, 1f);
                    }
                }
            }
            tex.SetPixels(colors);
            tex.Apply(false, false);
            return tex;
        }

        private static double ComputeRgba8Psnr(byte[] a, byte[] b)
        {
            double sq = 0.0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                double d = a[i] - b[i];
                sq += d * d;
            }
            double mse = n > 0 ? sq / n : 0.0;
            return mse > 1e-12 ? 10.0 * Math.Log10((255.0 * 255.0) / mse) : 99.99;
        }

        private static double ComputeMeanNormalAngularErrorDeg(Color[] refPixels, byte[] decodedRgba8)
        {
            double degSum = 0.0;
            int count = refPixels.Length;
            for (int i = 0; i < count; i++)
            {
                float ax = refPixels[i].r * 2f - 1f;
                float ay = refPixels[i].g * 2f - 1f;
                float az = Mathf.Sqrt(Mathf.Max(0f, 1f - ax * ax - ay * ay));

                // ASTC Normal map stores GGGR (R/G/B = ny, A = nx)
                float bx = (decodedRgba8[i * 4 + 3] / 255f) * 2f - 1f;
                float by = (decodedRgba8[i * 4 + 1] / 255f) * 2f - 1f;
                float bz = Mathf.Sqrt(Mathf.Max(0f, 1f - bx * bx - by * by));

                float dot = Mathf.Clamp(ax * bx + ay * by + az * bz, -1f, 1f);
                degSum += Math.Acos(dot) * (180.0 / Math.PI);
            }
            return count > 0 ? degSum / count : 0.0;
        }

        [Test]
        public void DecompressAsync_RoundTrip_MatchesSync()
        {
            if (!Encoder.IsCpuSupported)
                Assert.Ignore("CPU ASTC encoder not available.");

            Texture2D src = new Texture2D(32, 32, TextureFormat.RGBA32, false, linear: true);
            try
            {
                Color32[] pixels = new Color32[32 * 32];
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                        pixels[y * 32 + x] = new Color32((byte)(x * 8), (byte)(y * 8), (byte)((x ^ y) * 7), 255);
                src.SetPixels32(pixels);
                src.Apply(false, false);

                using AstcRaw raw = src.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast, backend: EncodeBackend.Cpu);
                Assert.IsNotNull(raw);

                byte[] syncDecoded = Encoder.Decompress(raw, 32, 32, BlockSize.ASTC_6x6);
                byte[] asyncDecoded = Encoder.DecompressAsync(raw, 32, 32, BlockSize.ASTC_6x6).GetAwaiter().GetResult();

                Assert.IsNotNull(syncDecoded);
                Assert.IsNotNull(asyncDecoded);
                Assert.AreEqual(syncDecoded.Length, asyncDecoded.Length);
                CollectionAssert.AreEqual(syncDecoded, asyncDecoded);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void CompressionQueue_DefaultConcurrency_And_DiskCacheDefaultDir_AreValid()
        {
            using var queue = new AstcCompressionQueue();
            Assert.GreaterOrEqual(queue.MaxConcurrentCpuTasks, 1);
            Assert.IsFalse(string.IsNullOrEmpty(AstcDiskCache.DefaultCacheDirectory));
        }
    }
}
