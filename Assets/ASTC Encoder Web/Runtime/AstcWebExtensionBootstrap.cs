using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Registers WebGL2 / Tuanjie MiniGame extension implementations into <see cref="AstcExtensionHooks"/>
    /// automatically on both Editor load and Player startup.
    /// </summary>
    internal static class AstcWebExtensionBootstrap
    {
        private sealed class ReadbackFallbackAdapter : IAstcReadbackFallback
        {
            public bool IsSupported => AstcReadbackBridge.IsSupported;
            public bool IsInputSupported(Texture texture) => AstcReadbackBridge.IsInputSupported(texture);
            public void Preload() => AstcReadbackBridge.Preload();
            public void ReleaseCache() => AstcReadbackBridge.ReleaseCache();

            public AstcRaw CompressTextureSync(
                Texture src, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight, bool generateMipmaps = false,
                int threadCount = 1, bool usePerceptual = false, uint alphaScaleRadius = 1u, bool useUnityJobSystem = false)
                => AstcReadbackBridge.CompressTextureSync(src, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps, threadCount, usePerceptual, alphaScaleRadius, useUnityJobSystem);

            public Task<AstcRaw> CompressTextureAsync(
                Texture src, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight, CancellationToken cancellationToken,
                bool generateMipmaps = false, int threadCount = 0, bool usePerceptual = false, uint alphaScaleRadius = 1u)
                => AstcReadbackBridge.CompressTextureAsync(src, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken, generateMipmaps, threadCount, usePerceptual, alphaScaleRadius);

            public Texture2D CompressToTextureSync(
                Texture source, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight, bool generateMipmaps = false,
                int threadCount = 1, bool usePerceptual = false, uint alphaScaleRadius = 1u, bool useUnityJobSystem = false)
                => AstcReadbackBridge.CompressToTextureSync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps, threadCount, usePerceptual, alphaScaleRadius, useUnityJobSystem);

            public Task<Texture2D> CompressToTextureAsync(
                Texture source, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight, CancellationToken cancellationToken,
                bool generateMipmaps = false, int threadCount = 0, bool usePerceptual = false, uint alphaScaleRadius = 1u)
                => AstcReadbackBridge.CompressToTextureAsync(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken, generateMipmaps, threadCount, usePerceptual, alphaScaleRadius);

            public bool CompressInPlace(
                Texture2D inputTexture, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool makeNoLongerReadable, bool useAlphaWeight, bool generateMipmaps = false,
                int threadCount = 1, bool usePerceptual = false, uint alphaScaleRadius = 1u, bool useUnityJobSystem = false)
                => AstcReadbackBridge.CompressInPlace(inputTexture, textureType, blockSize, quality, makeNoLongerReadable, useAlphaWeight, generateMipmaps, threadCount, usePerceptual, alphaScaleRadius, useUnityJobSystem);

            public Cubemap CompressCubemap(
                Cubemap source, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight)
                => AstcReadbackBridge.CompressCubemap(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);

            public Texture2DArray CompressTexture2DArray(
                Texture2DArray source, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                bool includeMipmaps, bool useAlphaWeight)
                => AstcReadbackBridge.CompressTexture2DArray(source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight);

            public void EncodeAtlasTile(
                Texture source, Texture destination, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                int sourceMip, int destMip, int destX, int destY, bool useAlphaWeight, int sourceSlice, int destSlice)
                => AstcAtlasShadowUpdater.Encode(source, destination, textureType, blockSize, quality, sourceMip, destMip, destX, destY, useAlphaWeight, sourceSlice, destSlice);
        }

        private sealed class WebExecutionAdapter : IAstcWebExecutionProvider
        {
            public bool IsWorkerSupported => AstcWebWorkerBackend.IsSupported;
            public bool IsWorkerReady => AstcWebWorkerBackend.IsReady;
            public bool IsWorkerSimdEnabled => AstcWebWorkerBackend.IsSimdEnabled;
            public int WorkerCount => AstcWebWorkerBackend.WorkerCount;
            public int DefaultConcurrency => AstcWebExecutor.DefaultConcurrency;
            public bool IsPthreadsActive => AstcWebExecutor.IsPthreadsActive;
            public bool CanUseNativeThreads => AstcWebPlatform.CanUseNativeThreads;
            public int HardwareConcurrency => AstcWebPlatform.HardwareConcurrency;

            public Task<bool> InitializeWorkerAsync(string workerScriptUrl, int maxWorkers)
                => AstcWebWorkerBackend.InitializeAsync(workerScriptUrl, maxWorkers);

            public void ShutdownWorker() => AstcWebWorkerBackend.Shutdown();

            public Task<AstcRaw> CompressPixelsAsync(
                IntPtr pixelPtr, int pixelByteLength, int width, int height, int mipCount,
                GraphicsFormat format, TextureType textureType, BlockSize blockSize, CompressQuality quality,
                int threadCount, bool useAlphaWeight, bool usePerceptual, uint alphaScaleRadius, CancellationToken cancellationToken)
                => AstcWebExecutor.CompressPixelsAsync(
                    pixelPtr, pixelByteLength, width, height, mipCount, format, textureType,
                    blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, cancellationToken);

            public Task<byte[]> DecompressAsync(
                IntPtr astcPtr, int astcByteLength, int width, int height, BlockSize blockSize,
                bool srgb, bool isHdr, int outputBitness, int threadCount, CancellationToken cancellationToken)
                => AstcWebExecutor.DecompressAsync(
                    astcPtr, astcByteLength, width, height, blockSize, srgb, isHdr, outputBitness, threadCount, cancellationToken);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Register()
        {
            AstcExtensionHooks.ReadbackFallback = new ReadbackFallbackAdapter();
            AstcExtensionHooks.WebExecutor = new WebExecutionAdapter();

#if UNITY_WEBGL && !UNITY_EDITOR
            AstcExtensionHooks.CacheStorage = AstcWebPlatform.IsMiniGame
                ? (IAstcCacheStorage)new MiniGameCacheStorage()
                : new IdbfsCacheStorage();
#endif
        }
    }
}
