using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Aperture.Astcenc.Runtime.NativeAPI;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Unified WebGL async CPU execution dispatcher.
    /// Priority: Emscripten pthreads (native C++ multi-threading) > Web Worker pool > main-thread inline fallback.
    /// </summary>
    internal static class AstcWebExecutor
    {
        private const int k_BytesPerBlock = 16;

        internal static int DefaultConcurrency
        {
            get
            {
                if (!AstcWebPlatform.IsWebPlayer)
                    return Math.Max(1, Environment.ProcessorCount / 2);

                if (IsPthreadsActive)
                    return Math.Max(1, Math.Min(AstcWebPlatform.HardwareConcurrency, 4));

                if (AstcWebWorkerBackend.IsReady)
                    return Math.Max(1, AstcWebWorkerBackend.WorkerCount);

                return 1;
            }
        }

        internal static bool IsPthreadsActive
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (!AstcWebPlatform.CanUseNativeThreads)
                    return false;
                try
                {
                    return AstcWebNativeApi.astcenc_wasm_mt_available() != 0;
                }
                catch
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private sealed class ActivePthreadJob
        {
            public int Handle;
            public bool IsDecompress;
            public IDisposable ContextLease;
            public TaskCompletionSource<AstcRaw> CompressTcs;
            public TaskCompletionSource<byte[]> DecompressTcs;
            public CancellationTokenRegistration CancelRegistration;
        }

        private static readonly List<ActivePthreadJob> s_ActivePthreadJobs = new List<ActivePthreadJob>();

        private static void PollPthreadJobs()
        {
            for (int i = s_ActivePthreadJobs.Count - 1; i >= 0; i--)
            {
                ActivePthreadJob job = s_ActivePthreadJobs[i];
                int status = AstcWebNativeApi.astcenc_wasm_mt_poll(job.Handle, out IntPtr outPtr, out UIntPtr outLen);
                if (status == 0)
                    continue;

                s_ActivePthreadJobs.RemoveAt(i);
                job.CancelRegistration.Dispose();

                try
                {
                    if (status == 1 && outPtr != IntPtr.Zero && outLen.ToUInt64() > 0)
                    {
                        int byteLen = checked((int)outLen.ToUInt64());
                        if (job.IsDecompress)
                        {
                            byte[] bytes = new byte[byteLen];
                            Marshal.Copy(outPtr, bytes, 0, byteLen);
                            job.DecompressTcs?.TrySetResult(bytes);
                        }
                        else
                        {
                            IntPtr owned = Marshal.AllocHGlobal(byteLen);
                            try
                            {
                                unsafe
                                {
                                    UnsafeUtility.MemCpy((void*)owned, (void*)outPtr, byteLen);
                                }
                                job.CompressTcs?.TrySetResult(new AstcRaw(owned, byteLen));
                            }
                            catch (Exception ex)
                            {
                                Marshal.FreeHGlobal(owned);
                                job.CompressTcs?.TrySetException(ex);
                            }
                        }
                    }
                    else
                    {
                        job.CompressTcs?.TrySetResult(null);
                        job.DecompressTcs?.TrySetResult(null);
                    }
                }
                finally
                {
                    AstcWebNativeApi.astcenc_wasm_mt_finish(job.Handle);
                    job.ContextLease?.Dispose();
                }
            }

            if (s_ActivePthreadJobs.Count == 0)
            {
                AstcMainThreadPump.UnregisterTick(PollPthreadJobs);
            }
        }
#endif

        internal static async Task<AstcRaw> CompressPixelsAsync(
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
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return await Task.FromCanceled<AstcRaw>(cancellationToken);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsPthreadsActive)
            {
                return await BeginPthreadCompressAsync(
                    pixelPtr, pixelByteLength, width, height, mipCount, format, textureType,
                    blockSize, quality, threadCount, useAlphaWeight, usePerceptual, alphaScaleRadius, cancellationToken);
            }

            if (await AstcWebWorkerBackend.EnsureInitializedAsync().ConfigureAwait(false))
            {
                return await AstcWebWorkerBackend.CompressSnapshotAsync(
                    pixelPtr, pixelByteLength, width, height, mipCount, format, textureType,
                    blockSize, quality, useAlphaWeight, usePerceptual, alphaScaleRadius, cancellationToken);
            }
#endif
            return await Task.FromResult<AstcRaw>(null);
        }

        internal static async Task<byte[]> DecompressAsync(
            IntPtr astcPtr,
            int astcByteLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            int threadCount,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return await Task.FromCanceled<byte[]>(cancellationToken);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsPthreadsActive)
            {
                return await BeginPthreadDecompressAsync(
                    astcPtr, astcByteLength, width, height, blockSize, srgb, isHdr, outputBitness, threadCount, cancellationToken);
            }

            if (await AstcWebWorkerBackend.EnsureInitializedAsync().ConfigureAwait(false))
            {
                return await AstcWebWorkerBackend.DecompressAsync(
                    astcPtr, astcByteLength, width, height, blockSize, srgb, isHdr, outputBitness, cancellationToken);
            }
#endif
            return await Task.FromResult<byte[]>(null);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private static unsafe Task<AstcRaw> BeginPthreadCompressAsync(
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
            CancellationToken cancellationToken)
        {
            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return Task.FromResult<AstcRaw>(null);

            long totalPixels = 0;
            long expectedOutBytes = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int mw = Math.Max(1, width >> m);
                int mh = Math.Max(1, height >> m);
                totalPixels += (long)mw * mh;
                long blocksX = (mw + (long)bx - 1L) / (long)bx;
                long blocksY = (mh + (long)by - 1L) / (long)by;
                expectedOutBytes += blocksX * blocksY * k_BytesPerBlock;
            }

            if (totalPixels <= 0 || expectedOutBytes <= 0 || expectedOutBytes > int.MaxValue)
                return Task.FromResult<AstcRaw>(null);

            int resolvedThreads = AstcContext.ResolveAsyncThreadCount(threadCount);
            AstcContextPool.CompressorLease lease = AstcContextPool.RentCompressor(
                format, textureType, blockSize, quality, resolvedThreads, useAlphaWeight, usePerceptual, alphaScaleRadius, false);
            if (lease.Context == null)
            {
                lease.Dispose();
                return Task.FromResult<AstcRaw>(null);
            }

            IntPtr rgbaBuffer = IntPtr.Zero;
            bool ownsRgbaBuffer = false;
            try
            {
                if (!AstcWebWorkerBackend.PrepareRgba4Buffer(
                    (void*)pixelPtr, pixelByteLength, (int)totalPixels, format,
                    out rgbaBuffer, out int rgbaByteLen, out int dataType, out _, out ownsRgbaBuffer))
                {
                    lease.Dispose();
                    return Task.FromResult<AstcRaw>(null);
                }

                astcenc_swizzle swz = AstcCpuBackend.GetSwizzle(format, textureType);
                int handle = AstcWebNativeApi.astcenc_wasm_mt_begin_compress(
                    lease.Context.Handle,
                    (uint)resolvedThreads,
                    bx,
                    by,
                    (int)swz.r,
                    (int)swz.g,
                    (int)swz.b,
                    (int)swz.a,
                    (uint)width,
                    (uint)height,
                    (uint)mipCount,
                    dataType,
                    rgbaBuffer,
                    (UIntPtr)rgbaByteLen,
                    (UIntPtr)expectedOutBytes);

                if (handle <= 0)
                {
                    lease.Dispose();
                    return Task.FromResult<AstcRaw>(null);
                }

                var tcs = new TaskCompletionSource<AstcRaw>();
                var job = new ActivePthreadJob
                {
                    Handle = handle,
                    IsDecompress = false,
                    ContextLease = lease,
                    CompressTcs = tcs
                };

                if (cancellationToken.CanBeCanceled)
                {
                    job.CancelRegistration = cancellationToken.Register(() =>
                    {
                        AstcWebNativeApi.astcenc_wasm_mt_cancel(handle);
                    });
                }

                s_ActivePthreadJobs.Add(job);
                AstcMainThreadPump.RegisterTick(PollPthreadJobs);
                return tcs.Task;
            }
            finally
            {
                if (ownsRgbaBuffer && rgbaBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(rgbaBuffer);
                }
            }
        }

        private static Task<byte[]> BeginPthreadDecompressAsync(
            IntPtr astcPtr,
            int astcByteLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            int threadCount,
            CancellationToken cancellationToken)
        {
            int bpp = outputBitness == 32 ? 16 : (outputBitness == 16 ? 8 : 4);
            long expectedOutLong = (long)width * height * bpp;
            if (expectedOutLong <= 0 || expectedOutLong > int.MaxValue)
                return Task.FromResult<byte[]>(null);

            int resolvedThreads = AstcContext.ResolveAsyncThreadCount(threadCount);
            AstcContextPool.DecompressorLease lease = AstcContextPool.RentDecompressor(blockSize, srgb, isHdr, resolvedThreads);
            if (lease.Context == null)
            {
                lease.Dispose();
                return Task.FromResult<byte[]>(null);
            }

            int astcType = outputBitness == 32 ? 2 : (outputBitness == 16 ? 1 : 0);
            int handle = AstcWebNativeApi.astcenc_wasm_mt_begin_decompress(
                lease.Context.Handle,
                (uint)resolvedThreads,
                (uint)width,
                (uint)height,
                astcType,
                astcPtr,
                (UIntPtr)astcByteLength,
                (UIntPtr)expectedOutLong);

            if (handle <= 0)
            {
                lease.Dispose();
                return Task.FromResult<byte[]>(null);
            }

            var tcs = new TaskCompletionSource<byte[]>();
            var job = new ActivePthreadJob
            {
                Handle = handle,
                IsDecompress = true,
                ContextLease = lease,
                DecompressTcs = tcs
            };

            if (cancellationToken.CanBeCanceled)
            {
                job.CancelRegistration = cancellationToken.Register(() =>
                {
                    AstcWebNativeApi.astcenc_wasm_mt_cancel(handle);
                });
            }

            s_ActivePthreadJobs.Add(job);
            AstcMainThreadPump.RegisterTick(PollPthreadJobs);
            return tcs.Task;
        }
#endif
    }
}
