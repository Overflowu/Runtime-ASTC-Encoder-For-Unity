using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Aperture.Astcenc.Runtime.NativeAPI;

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
        private int m_Disposed;

        public BlockSize BlockSize { get; }
        public CompressQuality Quality { get; }
        public int ThreadCount { get; }

        /// <summary>
        /// When true, schedules multi-threaded astcenc work via Unity's native <see cref="IJobParallelFor"/>
        /// Worker Thread Pool instead of .NET ThreadPool. Only enable when invoking from the Unity main thread.
        /// </summary>
        public bool UseUnityJobSystem { get; set; }

        internal uint BlockX { get; }
        internal uint BlockY { get; }
        internal astcenc_profile Profile { get; }
        internal CodecFlags Flags { get; }
        internal bool IsDecompressOnly => (Flags & CodecFlags.DecompressOnly) != 0;

        private unsafe struct AstcCompressParallelJob : IJobParallelFor
        {
            public IntPtr Context;
            public astcenc_image Image;
            public astcenc_swizzle Swizzle;
            public IntPtr Output;
            public UIntPtr OutputLength;
            public IntPtr FirstErrorPtr;

            public void Execute(int threadIndex)
            {
                astcenc_image localImage = Image;
                astcenc_swizzle localSwizzle = Swizzle;
                astcenc_error threadStatus = astcenc_api.astcenc_compress_image(
                    Context,
                    ref localImage,
                    ref localSwizzle,
                    Output,
                    OutputLength,
                    (uint)threadIndex);

                if (threadStatus != astcenc_error.ASTCENC_SUCCESS)
                {
                    Interlocked.CompareExchange(ref *(int*)FirstErrorPtr, (int)threadStatus, (int)astcenc_error.ASTCENC_SUCCESS);
                }
            }
        }

        private AstcContext(
            IntPtr context,
            astcenc_profile profile,
            BlockSize blockSize,
            uint blockX,
            uint blockY,
            CompressQuality quality,
            CodecFlags flags,
            int threadCount,
            bool useUnityJobSystem = false)
        {
            m_Context = context;
            Profile = profile;
            BlockSize = blockSize;
            BlockX = blockX;
            BlockY = blockY;
            Quality = quality;
            Flags = flags;
            ThreadCount = threadCount;
            UseUnityJobSystem = useUnityJobSystem;
            m_Disposed = 0;
        }

        ~AstcContext()
        {
            Dispose(false);
        }

        /// <summary>
        /// Creates a reusable ASTC compression context matching the given texture format and encoding settings.
        /// Pass threadCount &lt;= 0 to auto-select a sensible worker thread count based on CPU cores.
        /// Optionally pass <paramref name="parentContext"/> with matching block dimensions to share read-only lookup tables (astcenc 5.x).
        /// </summary>
        public static AstcContext Create(
            GraphicsFormat graphicsFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount = 1,
            bool useAlphaWeight = false,
            bool usePerceptual = false,
            uint alphaScaleRadius = 1u,
            AstcContext parentContext = null,
            bool useUnityJobSystem = false)
        {
            if (!AstcCpuBackend.IsSupported)
            {
                Debug.LogError("Native CPU ASTC compression library is not available on this platform.");
                return null;
            }

            if (!AstcCpuBackend.IsSupportGraphicFormat(graphicsFormat))
            {
                Debug.LogError("Unsupported GraphicsFormat: " + graphicsFormat);
                return null;
            }

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out uint blockZ))
            {
                Debug.LogError("Unsupported ASTC block size: " + blockSize);
                return null;
            }

            IntPtr parentPtr = IntPtr.Zero;
            if (parentContext != null)
            {
                parentContext.ThrowIfDisposed();
                if (parentContext.BlockX != blockX || parentContext.BlockY != blockY)
                {
                    Debug.LogError($"Parent AstcContext block size ({parentContext.BlockSize}) does not match requested block size ({blockSize}).");
                    return null;
                }
                parentPtr = parentContext.m_Context;
            }

            int resolvedThreads = threadCount <= 0
                ? Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
                : threadCount;

            float qualityValue = AstcCpuBackend.GetQualityValue(quality);
            astcenc_profile profile = AstcCpuBackend.GetColorProfile(graphicsFormat, textureType);
            CodecFlags flags = AstcCpuBackend.GetFlags(textureType, graphicsFormat, useAlphaWeight, usePerceptual);

            astcenc_config config = new astcenc_config();
            astcenc_error status = astcenc_api.astcenc_config_init(profile, blockX, blockY, blockZ, qualityValue, (uint)flags, ref config);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Codec config init failed:" + status);
                return null;
            }

            if ((flags & CodecFlags.UseAlphaWeight) != 0)
            {
                config.a_scale_radius = alphaScaleRadius;
            }

            IntPtr context = IntPtr.Zero;
            status = parentPtr != IntPtr.Zero
                ? astcenc_api.astcenc_context_alloc(IntPtr.Zero, (uint)resolvedThreads, ref context, parentPtr)
                : astcenc_api.astcenc_context_alloc(ref config, (uint)resolvedThreads, ref context, IntPtr.Zero);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Codec context alloc failed:" + status);
                return null;
            }

            return new AstcContext(context, profile, blockSize, blockX, blockY, quality, flags, resolvedThreads, useUnityJobSystem);
        }

        /// <summary>
        /// Creates a lightweight decompression-only ASTC context for CPU decoding via ARM astc-encoder.
        /// </summary>
        public static AstcContext CreateDecompressor(
            BlockSize blockSize,
            bool srgb = false,
            bool isHdr = false,
            int threadCount = 1)
        {
            if (!AstcCpuBackend.IsSupported)
            {
                Debug.LogError("Native CPU ASTC library is not available on this platform.");
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

            astcenc_profile profile = isHdr
                ? astcenc_profile.ASTCENC_PRF_HDR
                : (srgb ? astcenc_profile.ASTCENC_PRF_LDR_SRGB : astcenc_profile.ASTCENC_PRF_LDR);

            CodecFlags flags = CodecFlags.DecompressOnly;

            astcenc_config config = new astcenc_config();
            astcenc_error status = astcenc_api.astcenc_config_init(profile, blockX, blockY, blockZ, 0.0f, (uint)flags, ref config);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Decompress codec config init failed:" + status);
                return null;
            }

            IntPtr context = IntPtr.Zero;
            status = astcenc_api.astcenc_context_alloc(ref config, (uint)resolvedThreads, ref context, IntPtr.Zero);
            if (status != astcenc_error.ASTCENC_SUCCESS)
            {
                Debug.LogError("Decompress codec context alloc failed:" + status);
                return null;
            }

            return new AstcContext(context, profile, blockSize, blockX, blockY, CompressQuality.Fastest, flags, resolvedThreads);
        }

        public void ThrowIfDisposed()
        {
            if (Volatile.Read(ref m_Disposed) != 0)
                throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Synchronously compresses a Texture2D reusing this codec context.
        /// </summary>
        public AstcRaw CompressTexture(Texture2D inputTexture, TextureType textureType = TextureType.Default)
        {
            ThrowIfDisposed();
            return AstcCpuBackend.CompressTextureWithContext(inputTexture, this, textureType, true);
        }

        /// <summary>
        /// Captures pixel data on the calling (main) thread and compresses asynchronously on background thread(s) reusing this context.
        /// Caller must keep this AstcContext alive until the returned Task completes and dispose the returned AstcRaw after use.
        /// </summary>
        public Task<AstcRaw> CompressTextureAsync(Texture2D inputTexture, TextureType textureType = TextureType.Default, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return AstcCpuBackend.CompressTextureWithContextAsync(inputTexture, this, textureType, true, cancellationToken);
        }

        internal bool IsCompatibleWith(GraphicsFormat graphicsFormat, TextureType textureType)
        {
            if (IsDecompressOnly)
                return false;

            bool isNormalContext = (Flags & CodecFlags.MapNormal) != 0;
            return AstcCpuBackend.GetColorProfile(graphicsFormat, textureType) == Profile
                && isNormalContext == (textureType == TextureType.Normal);
        }

        internal astcenc_error DecompressSingleImage(IntPtr compressedData, int compressedLength, ref astcenc_image imageOut, ref astcenc_swizzle swizzle)
        {
            lock (m_SyncLock)
            {
                ThrowIfDisposed();

                astcenc_error status;
                try
                {
                    if (ThreadCount == 1 || compressedLength <= k_ParallelMinOutputBytes)
                    {
                        status = astcenc_api.astcenc_decompress_image(
                            m_Context,
                            compressedData,
                            new UIntPtr((uint)compressedLength),
                            ref imageOut,
                            ref swizzle,
                            0);
                    }
                    else
                    {
                        astcenc_image imageCopy = imageOut;
                        astcenc_swizzle swizzleCopy = swizzle;
                        UIntPtr dataLen = new UIntPtr((uint)compressedLength);
                        int firstError = (int)astcenc_error.ASTCENC_SUCCESS;

                        Parallel.For(0, ThreadCount, threadIndex =>
                        {
                            astcenc_image localImage = imageCopy;
                            astcenc_swizzle localSwizzle = swizzleCopy;
                            astcenc_error threadStatus = astcenc_api.astcenc_decompress_image(
                                m_Context,
                                compressedData,
                                dataLen,
                                ref localImage,
                                ref localSwizzle,
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
                    astcenc_api.astcenc_decompress_reset(m_Context);
                }

                return status;
            }
        }

        internal astcenc_error CompressSingleImage(ref astcenc_image image, ref astcenc_swizzle swizzle, IntPtr output, int outputLength, CancellationToken cancellationToken = default)
        {
            lock (m_SyncLock)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                astcenc_error status;
                using (CancellationTokenRegistration cancelRegistration = cancellationToken.CanBeCanceled
                    ? cancellationToken.Register(() => astcenc_api.astcenc_compress_cancel(m_Context))
                    : default)
                {
                    try
                    {
                        if (ThreadCount == 1 || outputLength <= k_ParallelMinOutputBytes)
                        {
                            status = astcenc_api.astcenc_compress_image(m_Context, ref image, ref swizzle, output, new UIntPtr((uint)outputLength), 0);
                        }
                        else if (UseUnityJobSystem)
                        {
                            int firstError = (int)astcenc_error.ASTCENC_SUCCESS;
                            unsafe
                            {
                                int* errPtr = &firstError;
                                var job = new AstcCompressParallelJob
                                {
                                    Context = m_Context,
                                    Image = image,
                                    Swizzle = swizzle,
                                    Output = output,
                                    OutputLength = new UIntPtr((uint)outputLength),
                                    FirstErrorPtr = (IntPtr)errPtr,
                                };
                                job.Schedule(ThreadCount, 1).Complete();
                            }
                            status = (astcenc_error)firstError;
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
                }

                cancellationToken.ThrowIfCancellationRequested();
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
            if (Interlocked.Exchange(ref m_Disposed, 1) == 0)
            {
                IntPtr context = Interlocked.Exchange(ref m_Context, IntPtr.Zero);
                if (context != IntPtr.Zero)
                {
                    astcenc_api.astcenc_context_free(context);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
