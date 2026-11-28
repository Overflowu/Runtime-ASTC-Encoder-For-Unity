using System;
using System.Collections;
using System.IO;
using Aperture.Astcenc.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;

namespace Aperture.Astcenc.Tests.PlayMode
{
    public class AstcEncoderPlayModeTests
    {
        [UnityTest]
        public IEnumerator PlayMode_DynamicTextureCompressAndAstcFileRoundTrip_Succeeds()
        {
            Texture2D tex = new Texture2D(48, 32, TextureFormat.RGBA32, mipChain: true, linear: false);
            try
            {
                Color[] pixels = new Color[48 * 32];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color((i % 48) / 47.0f, (i / 48) / 31.0f, 0.5f, 1.0f);
                }
                tex.SetPixels(pixels);
                tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);

                yield return null;

                Assert.IsTrue(Encoder.IsSupport(tex));

                using (AstcRaw raw = tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast))
                {
                    Assert.IsNotNull(raw);
                    Assert.Greater(raw.Length, 0);
                }

                yield return null;

                Texture2D singleMip = new Texture2D(32, 32, TextureFormat.RGBA32, mipChain: false, linear: false);
                try
                {
                    singleMip.SetPixels(new Color[32 * 32]);
                    singleMip.Apply(false, false);

                    byte[] astcBytes = singleMip.EncodeToASTC(TextureType.Default, BlockSize.ASTC_8x8, CompressQuality.Fastest);
                    Assert.IsNotNull(astcBytes);
                    Assert.AreEqual(16 + (4 * 4 * 16), astcBytes.Length);
                    Assert.AreEqual(0x13, astcBytes[0]);
                    Assert.AreEqual(0xAB, astcBytes[1]);
                    Assert.AreEqual(0xA1, astcBytes[2]);
                    Assert.AreEqual(0x5C, astcBytes[3]);

                    string outPath = Path.Combine(Application.persistentDataPath, "playmode_e2e_test.astc");
                    File.WriteAllBytes(outPath, astcBytes);
                    byte[] diskBytes = File.ReadAllBytes(outPath);
                    Assert.AreEqual(astcBytes.Length, diskBytes.Length);
                    File.Delete(outPath);
                }
                finally
                {
                    UnityEngine.Object.Destroy(singleMip);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        [UnityTest]
        public IEnumerator PlayMode_InPlaceCompress_SucceedsWhenAstcSupported()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.RGBA_ASTC6X6_UNorm, FormatUsage.Sample))
            {
                Assert.Ignore($"ASTC GPU texture sampling is not supported on current graphics device ({SystemInfo.graphicsDeviceType}).");
                yield break;
            }

            Texture2D tex = new Texture2D(32, 32, TextureFormat.RGBA32, mipChain: false, linear: true);
            try
            {
                tex.SetPixels(new Color[32 * 32]);
                tex.Apply(false, false);

                yield return null;

                bool ok = tex.Compress(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest, makeNoLongerReadable: true);
                Assert.IsTrue(ok);
                Assert.AreEqual(GraphicsFormat.RGBA_ASTC6X6_UNorm, tex.graphicsFormat);
                Assert.IsFalse(tex.isReadable);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
