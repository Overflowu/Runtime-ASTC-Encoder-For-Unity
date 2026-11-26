using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Reusable native astcenc codec context supporting single-threaded, multi-threaded, and async compression.
    /// Reusing a context across multiple textures avoids repeated astcenc_context_alloc setup overhead.
    /// </summary>
    public sealed class AstcContext : IDisposable
    {
        private const int k_ParallelMinOutputBytes = 64 * 16;

        private readonly object m_SyncLock = new object();
        private IntPtr m_Context;
        private bool m_Disposed;

        public BlockSize BlockSize { get; }
        public CompressQuality Quality { get; }
        public int ThreadCount { get; }

        internal uint BlockX { get; }
        internal uint BlockY { get; }
        internal astcenc_profile Profile { get; }
        internal CodecFlags Flags { get; }

        private AstcContext(
            IntPtr context,
            astcenc_profile profile,
            BlockSize blockSize,
            uint blockX,
            uint blockY,
            CompressQuality quality,
            CodecFlags flags,
            int threadCount)
        {
            m_Context = context;
            Profile = profile;
            BlockSize = blockSize;
            BlockX = blockX;
            BlockY = blockY;
            Quality = quality;
            Flags = flags;
            ThreadCount = threadCount;
            m_Disposed = false;
        }

        ~AstcContext()
        {
            Dispose(false);
        }

        /// <summary>
        /// Creates a reusable ASTC compression context matching the given texture format and encoding settings.
        /// Pass threadCount &lt;= 0 to auto-select a sensible worker thread count based on CPU cores.
        /// </summary>
        public static AstcContext Create(
            GraphicsFormat graphicsFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount = 1)
        {
            if (!Encoder.IsSupportGraphicFormat(graphicsFormat))
            {
                Debug.LogError("Unsupported GraphicsFormat: " + graphicsFormat);
                return null;
            }

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out uint blockZ))
            {
                Debug.LogError("Unsupported ASTC block size: " + blockSize);
                return null;
            }

            int resolvedThreads = threadCount <= 0
                ? Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
                : threadCount;

            float qualityValue = Encoder.GetQualityValue(quality);
            astcenc_profile profile = Encoder.GetColorProfile(graphicsFormat);
            CodecFlags flags = Encoder.GetFlags(textureType, graphicsFormat);

            astcenc_config config = new astcenc_config();
            astcenc_error status = astcenc_api.astcenc_config_init(profile, blockX, blockY, blockZ, qualityValue, (uint)flags, ref config);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Codec config init failed:" + status);
                return null;
            }

            IntPtr context = IntPtr.Zero;
            status = astcenc_api.astcenc_context_alloc(ref config, (uint)resolvedThreads, ref context, IntPtr.Zero);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Codec context alloc failed:" + status);
                return null;
            }

            return new AstcContext(context, profile, blockSize, blockX, blockY, quality, flags, resolvedThreads);
        }

        public void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Synchronously compresses a Texture2D reusing this codec context.
        /// </summary>
        public AstcRaw CompressTexture(Texture2D inputTexture, TextureType textureType = TextureType.Default)
        {
            ThrowIfDisposed();
            return Encoder.CompressTextureWithContext(inputTexture, this, textureType, true);
        }

        /// <summary>
        /// Captures pixel data on the calling (main) thread and compresses asynchronously on background thread(s) reusing this context.
        /// Caller must keep this AstcContext alive until the returned Task completes and dispose the returned AstcRaw after use.
        /// </summary>
        public Task<AstcRaw> CompressTextureAsync(Texture2D inputTexture, TextureType textureType = TextureType.Default)
        {
            ThrowIfDisposed();
            return Encoder.CompressTextureWithContextAsync(inputTexture, this, textureType, true);
        }

        internal bool IsCompatibleWith(GraphicsFormat graphicsFormat, TextureType textureType)
        {
            return Encoder.GetColorProfile(graphicsFormat) == Profile
                && Encoder.GetFlags(textureType, graphicsFormat) == Flags;
        }

        internal astcenc_error CompressSingleImage(ref astcenc_image image, ref astcenc_swizzle swizzle, IntPtr output, int outputLength)
        {
            lock (m_SyncLock)
            {
                ThrowIfDisposed();

                astcenc_error status;
                try
                {
                    if (ThreadCount == 1 || outputLength <= k_ParallelMinOutputBytes)
                    {
                        status = astcenc_api.astcenc_compress_image(m_Context, ref image, ref swizzle, output, new UIntPtr((uint)outputLength), 0);
                    }
                    else
                    {
                        astcenc_image imageCopy = image;
                        astcenc_swizzle swizzleCopy = swizzle;
                        UIntPtr dataLen = new UIntPtr((uint)outputLength);
                        int firstError = (int)astcenc_error.ASTCENC_SUCCESS;

                        Parallel.For(0, ThreadCount, threadIndex =>
                        {
                            astcenc_image localImage = imageCopy;
                            astcenc_swizzle localSwizzle = swizzleCopy;
                            astcenc_error threadStatus = astcenc_api.astcenc_compress_image(
                                m_Context,
                                ref localImage,
                                ref localSwizzle,
                                output,
                                dataLen,
                                (uint)threadIndex);

                            if (threadStatus != astcenc_error.ASTCENC_SUCCESS)
                            {
                                Interlocked.CompareExchange(ref firstError, (int)threadStatus, (int)astcenc_error.ASTCENC_SUCCESS);
                            }
                        });

                        status = (astcenc_error)firstError;
                    }
                }
                finally
                {
                    astcenc_api.astcenc_compress_reset(m_Context);
                }

                return status;
            }
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (m_SyncLock)
                {
                    FreeNativeContext();
                }
            }
            else
            {
                FreeNativeContext();
            }
        }

        private void FreeNativeContext()
        {
            if (!m_Disposed)
            {
                if (m_Context != IntPtr.Zero)
                {
                    astcenc_api.astcenc_context_free(m_Context);
                    m_Context = IntPtr.Zero;
                }
                m_Disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
