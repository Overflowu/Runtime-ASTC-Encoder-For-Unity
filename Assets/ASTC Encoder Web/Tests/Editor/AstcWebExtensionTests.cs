using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Aperture.Astcenc.Runtime;

namespace Aperture.Astcenc.Web.Tests
{
    public class AstcWebExtensionTests
    {
        [Test]
        public void WebExtension_AutoRegisters_Into_CoreHooks()
        {
            Assert.IsNotNull(AstcExtensionHooks.ReadbackFallback);
            Assert.IsNotNull(AstcExtensionHooks.WebExecutor);
            Assert.IsNotNull(AstcExtensionHooks.CacheStorage);
        }

        [Test]
        public void ReadbackBridge_RenderTexture_And_GenerateMipmaps_Works()
        {
            if (!Encoder.IsCpuSupported || !SystemInfo.supportsRenderTextures)
                Assert.Ignore("CPU ASTC encoder or RenderTextures not available.");

            var rt = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            Texture2D seed = new Texture2D(32, 32, TextureFormat.RGBA32, false, linear: true);
            try
            {
                Color32[] pixels = new Color32[32 * 32];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32((byte)(i & 255), (byte)((i * 3) & 255), 128, 255);
                seed.SetPixels32(pixels);
                seed.Apply(false, false);
                Graphics.Blit(seed, rt);

                AstcReadbackBridge.ForceEnableForTests = true;
                Assert.IsTrue(AstcReadbackBridge.IsSupported);

                using AstcRaw raw = AstcReadbackBridge.CompressTextureSync(
                    rt, TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast,
                    includeMipmaps: true, useAlphaWeight: false, generateMipmaps: true);
                Assert.IsNotNull(raw);
                Assert.AreEqual(6, AstcKtxContainer.InferMipCount(raw.Length, 32, 32, BlockSize.ASTC_6x6));
            }
            finally
            {
                AstcReadbackBridge.ForceEnableForTests = false;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(seed);
            }
        }

        [Test]
        public void ReadbackBridge_NonReadableTexture_And_AtlasShadowUpdate_Works()
        {
            if (!Encoder.IsCpuSupported || !SystemInfo.supportsRenderTextures)
                Assert.Ignore("CPU ASTC encoder or RenderTextures not available.");

            Texture2D nonReadable = new Texture2D(32, 32, TextureFormat.RGBA32, false, linear: true);
            Texture2D tile = new Texture2D(16, 16, TextureFormat.RGBA32, false, linear: true);
            Texture2D astcAtlas = new Texture2D(32, 32, GraphicsFormat.RGBA_ASTC4X4_UNorm, TextureCreationFlags.None);
            try
            {
                Color32[] p = new Color32[32 * 32];
                for (int i = 0; i < p.Length; i++) p[i] = new Color32(200, 100, 50, 255);
                nonReadable.SetPixels32(p);
                nonReadable.Apply(false, makeNoLongerReadable: true);

                Color32[] tp = new Color32[16 * 16];
                for (int i = 0; i < tp.Length; i++) tp[i] = new Color32(10, 220, 30, 255);
                tile.SetPixels32(tp);
                tile.Apply(false, false);

                AstcReadbackBridge.ForceEnableForTests = true;
                using AstcRaw raw = AstcReadbackBridge.CompressTextureSync(
                    nonReadable, TextureType.Default, BlockSize.ASTC_4x4, CompressQuality.Fast,
                    includeMipmaps: false, useAlphaWeight: false);
                Assert.IsNotNull(raw);
                Assert.AreEqual(8 * 8 * 16, raw.Length);

                AstcAtlasShadowUpdater.Register(astcAtlas, raw, BlockSize.ASTC_4x4);
                AstcAtlasShadowUpdater.Encode(tile, astcAtlas, TextureType.Default, BlockSize.ASTC_4x4, CompressQuality.Fast,
                    sourceMip: 0, destMip: 0, destX: 16, destY: 16, useAlphaWeight: false, sourceSlice: 0, destSlice: 0);
            }
            finally
            {
                AstcReadbackBridge.ForceEnableForTests = false;
                Object.DestroyImmediate(nonReadable);
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(astcAtlas);
            }
        }
    }
}
