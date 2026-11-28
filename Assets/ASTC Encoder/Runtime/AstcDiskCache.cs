using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Persistent on-disk ASTC texture cache backed by standard KTX v1 files (with full mipmap chains).
    /// Enables "compress once, load instantly on subsequent launches" workflows for downloaded or procedural textures.
    /// On WebGL, automatically persists writes to IndexedDB via IDBFS (or MiniGame FileSystemManager on Tuanjie MiniGames).
    /// </summary>
    public static class AstcDiskCache
    {
        /// <summary>
        /// Recommended default cache directory for the current platform
        /// (<c>Application.persistentDataPath/AstcCache</c> on native/WebGL, or <c>USER_DATA_PATH/astc-cache</c> on MiniGames).
        /// </summary>
        public static string DefaultCacheDirectory => AstcExtensionHooks.CacheStorage.DefaultRoot;

        /// <summary>
        /// Flushes any pending asynchronous filesystem writes to persistent browser storage (IDBFS <c>FS.syncfs</c> on WebGL).
        /// No-op on native platforms and MiniGames where writes are already flushed.
        /// </summary>
        public static Task FlushAsync() => AstcExtensionHooks.CacheStorage.FlushAsync();

        /// <summary>
        /// Attempts to load a cached KTX v1 ASTC texture from <paramref name="cachePath"/>.
        /// Returns null if the file does not exist or is corrupted.
        /// </summary>
        public static Texture2D TryLoad(string cachePath, bool makeNoLongerReadable = true, string textureName = null)
        {
            IAstcCacheStorage storage = AstcExtensionHooks.CacheStorage;
            if (!storage.Exists(cachePath))
                return null;

            try
            {
                byte[] bytes = storage.ReadAllBytes(cachePath);
                return AstcKtxContainer.LoadTexture(bytes, makeNoLongerReadable, textureName);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load cached ASTC KTX from '{cachePath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Asynchronously loads a cached KTX v1 ASTC texture from disk (file I/O off the main thread, GPU upload on caller thread).
        /// </summary>
        public static async Task<Texture2D> TryLoadAsync(string cachePath, bool makeNoLongerReadable = true, string textureName = null, CancellationToken cancellationToken = default)
        {
            IAstcCacheStorage storage = AstcExtensionHooks.CacheStorage;
            if (!storage.Exists(cachePath))
                return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes = await storage.ReadAllBytesAsync(cachePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return AstcKtxContainer.LoadTexture(bytes, makeNoLongerReadable, textureName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to asynchronously load cached ASTC KTX from '{cachePath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Compresses <paramref name="source"/> asynchronously, saves the resulting KTX v1 file atomically to <paramref name="cachePath"/>,
        /// and returns the GPU-ready ASTC <see cref="Texture2D"/>.
        /// </summary>
        public static async Task<Texture2D> CompressAndSaveAsync(
            Texture source,
            string cachePath,
            AstcCompressOptions options,
            CancellationToken cancellationToken = default)
        {
            if (source == null)
                return null;

            int width = source.width;
            int height = source.height;
            string texName = source.name;
            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(source.graphicsFormat, options.BlockSize, options.TextureType);

            using AstcRaw raw = await source.CompressTextureAsync(options, cancellationToken);
            if (raw == null)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, width, height, options.BlockSize);
            if (mipCount <= 0)
                return null;

            byte[] ktxBytes = AstcKtxContainer.Encode(raw, width, height, mipCount, targetFormat);

            if (!string.IsNullOrEmpty(cachePath))
            {
                await AstcExtensionHooks.CacheStorage.WriteAtomicAsync(cachePath, ktxBytes, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Encoder.CanCreateAstcTexture(targetFormat))
            {
                TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
                var tex = new Texture2D(width, height, targetFormat, mipCount, flags);
                try
                {
                    if (!string.IsNullOrEmpty(texName))
                        tex.name = texName;
                    tex.LoadRawTextureData(raw.Data, raw.Length);
                    tex.Apply(false, options.MakeNoLongerReadable);
                    return tex;
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    throw;
                }
            }

            return AstcKtxContainer.LoadTexture(ktxBytes, options.MakeNoLongerReadable, texName);
        }

        /// <summary>
        /// Loads the cached ASTC texture from <paramref name="cachePath"/> if present and valid;
        /// otherwise invokes <paramref name="sourceProvider"/>, compresses to ASTC, saves atomically to disk, and returns the texture.
        /// </summary>
        public static async Task<Texture2D> LoadOrCompressAsync(
            string cachePath,
            Func<Task<Texture>> sourceProvider,
            AstcCompressOptions options,
            bool destroySourceAfterCompress = false,
            CancellationToken cancellationToken = default)
        {
            Texture2D cached = await TryLoadAsync(cachePath, options.MakeNoLongerReadable, null, cancellationToken);
            if (cached != null)
                return cached;

            if (sourceProvider == null)
                return null;

            Texture source = await sourceProvider();
            if (source == null)
                return null;

            try
            {
                return await CompressAndSaveAsync(source, cachePath, options, cancellationToken);
            }
            finally
            {
                if (destroySourceAfterCompress && source != null)
                {
                    UnityEngine.Object.Destroy(source);
                }
            }
        }
    }
}
