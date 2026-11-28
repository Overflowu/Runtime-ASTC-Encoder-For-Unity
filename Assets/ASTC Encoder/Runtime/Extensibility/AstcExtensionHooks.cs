using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[assembly: InternalsVisibleTo("Aperture.Astcenc.Tests")]
[assembly: InternalsVisibleTo("Aperture.Astcenc.Web.Runtime")]
[assembly: InternalsVisibleTo("Aperture.Astcenc.Web.Editor")]
[assembly: InternalsVisibleTo("Aperture.Astcenc.Web.Tests")]

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Extension hook for GPU-readback fallback on platforms without Compute Shaders (e.g., WebGL2 / MiniGames).
    /// Implemented by the optional <c>com.aperture.astcenc.web</c> package.
    /// </summary>
    internal interface IAstcReadbackFallback
    {
        bool IsSupported { get; }
        bool IsInputSupported(Texture texture);
        void Preload();
        void ReleaseCache();

        AstcRaw CompressTextureSync(
            Texture src,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            bool generateMipmaps = false,
            int threadCount = 1,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false);

        Task<AstcRaw> CompressTextureAsync(
            Texture src,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            CancellationToken cancellationToken,
            bool generateMipmaps = false,
            int threadCount = 0,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u);

        Texture2D CompressToTextureSync(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            bool generateMipmaps = false,
            int threadCount = 1,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false);

        Task<Texture2D> CompressToTextureAsync(
            Texture source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            CancellationToken cancellationToken,
            bool generateMipmaps = false,
            int threadCount = 0,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u);

        bool CompressInPlace(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool makeNoLongerReadable,
            bool useAlphaWeight,
            bool generateMipmaps = false,
            int threadCount = 1,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            bool useUnityJobSystem = false);

        Cubemap CompressCubemap(
            Cubemap source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight);

        Texture2DArray CompressTexture2DArray(
            Texture2DArray source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight);

        void EncodeAtlasTile(
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
            int sourceSlice,
            int destSlice);
    }

    /// <summary>
    /// Extension hook for WebGL / MiniGame async CPU execution (pthreads / Web Worker pool).
    /// Implemented by the optional <c>com.aperture.astcenc.web</c> package.
    /// </summary>
    internal interface IAstcWebExecutionProvider
    {
        bool IsWorkerSupported { get; }
        bool IsWorkerReady { get; }
        bool IsWorkerSimdEnabled { get; }
        int WorkerCount { get; }
        int DefaultConcurrency { get; }
        bool IsPthreadsActive { get; }
        bool CanUseNativeThreads { get; }
        int HardwareConcurrency { get; }

        Task<bool> InitializeWorkerAsync(string workerScriptUrl, int maxWorkers);
        void ShutdownWorker();

        Task<AstcRaw> CompressPixelsAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            int mipCount,
            GraphicsFormat format,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken);

        Task<byte[]> DecompressAsync(
            IntPtr astcPtr,
            int astcByteLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            int threadCount,
            CancellationToken cancellationToken);
    }

    internal interface IAstcCacheStorage
    {
        string DefaultRoot { get; }
        bool Exists(string path);
        byte[] ReadAllBytes(string path);
        Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);
        Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken);
        Task FlushAsync();
    }

    internal sealed class FileCacheStorage : IAstcCacheStorage
    {
        public string DefaultRoot => Path.Combine(Application.persistentDataPath, "AstcCache");

        public bool Exists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.ReadAllBytes(path), cancellationToken);
        }

        public Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
        {
            return Task.Run(() => WriteAtomicSync(path, data, cancellationToken), cancellationToken);
        }

        public Task FlushAsync() => Task.CompletedTask;

        internal static void WriteAtomicSync(string path, byte[] data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, data);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    try
                    {
                        File.Move(tempPath, path);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null);
                    }
                }
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Central registry allowing optional extension packages (such as <c>com.aperture.astcenc.web</c>)
    /// to plug into <see cref="Encoder"/>, <see cref="AstcCpuBackend"/>, <see cref="AstcCompressionQueue"/>,
    /// and <see cref="AstcDiskCache"/> without introducing hard assembly dependencies in the core package.
    /// </summary>
    internal static class AstcExtensionHooks
    {
        private static readonly FileCacheStorage s_DefaultCacheStorage = new FileCacheStorage();

        internal static IAstcReadbackFallback ReadbackFallback { get; set; }
        internal static IAstcWebExecutionProvider WebExecutor { get; set; }
        internal static IAstcCacheStorage CacheStorage { get; set; } = s_DefaultCacheStorage;

        internal static int DefaultCpuConcurrency =>
            WebExecutor != null
                ? WebExecutor.DefaultConcurrency
                : Math.Max(1, Environment.ProcessorCount / 2);
    }
}
