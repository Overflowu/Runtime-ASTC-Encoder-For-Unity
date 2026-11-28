using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Aperture.Astcenc.Runtime.NativeAPI;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Non-blocking WebGL Web Worker pool backend for CPU ASTC compression.
    /// Offloads ASTC encoding to dedicated browser Web Workers using zero-copy Transferable ArrayBuffers
    /// (no SharedArrayBuffer / COOP / COEP headers required) with automatic WASM SIMD128 runtime dispatch.
    /// Falls back transparently to the built-in static WASM backend when not initialized.
    /// </summary>
    internal static class AstcWebWorkerBackend
    {
        private const int k_BytePerBlock = 16;

#if UNITY_WEBGL && !UNITY_EDITOR
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnReadyDelegate(int ok, int simdEnabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnCompleteDelegate(int jobId, int status, IntPtr dataPtr, int byteLength);

        [DllImport("__Internal")]
        private static extern int AstcWebWorker_IsSupported();

        [DllImport("__Internal")]
        private static extern int AstcWebWorker_Init(string workerUrl, int maxWorkers, OnReadyDelegate onReady, OnCompleteDelegate onComplete);

        [DllImport("__Internal")]
        private static extern int AstcWebWorker_CompressAsync(
            int jobId,
            int profile,
            uint blockX,
            uint blockY,
            float quality,
            uint flags,
            uint alphaScaleRadius,
            int swzR,
            int swzG,
            int swzB,
            int swzA,
            uint width,
            uint height,
            uint mipCount,
            int dataType,
            IntPtr rgbaPtr,
            int rgbaByteLen,
            int expectedOutLen);

        [DllImport("__Internal")]
        private static extern int AstcWebWorker_DecompressAsync(
            int jobId,
            int profile,
            uint blockX,
            uint blockY,
            uint width,
            uint height,
            int outBitness,
            IntPtr astcPtr,
            int astcByteLen,
            int expectedOutLen);

        [DllImport("__Internal")]
        private static extern int AstcWebWorker_GetWorkerCount();

        [DllImport("__Internal")]
        private static extern void AstcWebWorker_Cancel(int jobId);

        [DllImport("__Internal")]
        private static extern void AstcWebWorker_Shutdown();

        private sealed class PendingJob
        {
            public TaskCompletionSource<AstcRaw> CompressTcs;
            public TaskCompletionSource<byte[]> DecompressTcs;
            public CancellationTokenRegistration CancelRegistration;
        }

        [Serializable]
        private sealed class RuntimeConfigJson
        {
            public bool autoInit = true;
            public int maxWorkers = 0;
            public string workerUrl = "";
        }

        private static readonly Dictionary<int, PendingJob> s_PendingJobs = new Dictionary<int, PendingJob>();
        private static TaskCompletionSource<bool> s_InitTcs;
        private static int s_NextJobId;
        private static bool s_IsReady;
        private static bool s_IsSimdEnabled;

        internal static bool IsSupported
        {
            get
            {
                try
                {
                    return AstcWebWorker_IsSupported() != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static bool IsReady => s_IsReady;
        internal static bool IsSimdEnabled => s_IsSimdEnabled;
        internal static int WorkerCount
        {
            get
            {
                if (!s_IsReady) return 0;
                try { return Math.Max(1, AstcWebWorker_GetWorkerCount()); }
                catch { return 1; }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shutdown();
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitializeOnLoad()
        {
            if (s_IsReady || s_InitTcs != null || !IsSupported)
                return;

            // If native pthreads are available and active, prefer pthreads and skip eager worker startup.
            if (AstcWebPlatform.CanUseNativeThreads)
                return;

            TextAsset cfgAsset = Resources.Load<TextAsset>("AstcWebRuntimeConfig");
            if (cfgAsset == null)
                return;

            try
            {
                RuntimeConfigJson cfg = JsonUtility.FromJson<RuntimeConfigJson>(cfgAsset.text);
                if (cfg != null && cfg.autoInit)
                {
                    _ = InitializeAsync(string.IsNullOrEmpty(cfg.workerUrl) ? null : cfg.workerUrl, cfg.maxWorkers);
                }
            }
            catch
            {
                // Ignore malformed config; manual InitializeWebWorkerAsync remains available.
            }
        }

        internal static Task<bool> EnsureInitializedAsync()
        {
            if (s_IsReady)
                return Task.FromResult(true);
            if (s_InitTcs != null)
                return s_InitTcs.Task;
            return Task.FromResult(false);
        }

        internal static Task<bool> InitializeAsync(string workerScriptUrl = null, int maxWorkers = 0)
        {
            if (s_IsReady)
                return Task.FromResult(true);

            if (s_InitTcs != null)
                return s_InitTcs.Task;

            if (!IsSupported)
                return Task.FromResult(false);

            string resolvedUrl = string.IsNullOrEmpty(workerScriptUrl)
                ? CombineStreamingAssetsUrl("AstcWebWorker/astcenc-worker.js")
                : workerScriptUrl;

            s_InitTcs = new TaskCompletionSource<bool>();
            int started = AstcWebWorker_Init(resolvedUrl, maxWorkers, OnWorkerReady, OnWorkerComplete);
            if (started == 0)
            {
                TaskCompletionSource<bool> failed = s_InitTcs;
                s_InitTcs = null;
                failed.TrySetResult(false);
                return failed.Task;
            }

            return s_InitTcs.Task;
        }

        internal static void Shutdown()
        {
            try
            {
                if (s_IsReady || s_InitTcs != null)
                {
                    AstcWebWorker_Shutdown();
                }
            }
            catch
            {
                // Ignore teardown errors during page unload.
            }

            s_IsReady = false;
            s_IsSimdEnabled = false;

            if (s_InitTcs != null)
            {
                s_InitTcs.TrySetResult(false);
                s_InitTcs = null;
            }

            foreach (var kv in s_PendingJobs)
            {
                kv.Value.CancelRegistration.Dispose();
                kv.Value.CompressTcs?.TrySetCanceled();
                kv.Value.DecompressTcs?.TrySetCanceled();
            }
            s_PendingJobs.Clear();
        }

        [MonoPInvokeCallback(typeof(OnReadyDelegate))]
        private static void OnWorkerReady(int ok, int simdEnabled)
        {
            s_IsReady = ok != 0;
            s_IsSimdEnabled = s_IsReady && (simdEnabled != 0);

            TaskCompletionSource<bool> tcs = s_InitTcs;
            s_InitTcs = null;
            tcs?.TrySetResult(s_IsReady);
        }

        [MonoPInvokeCallback(typeof(OnCompleteDelegate))]
        private static void OnWorkerComplete(int jobId, int status, IntPtr dataPtr, int byteLength)
        {
            if (!s_PendingJobs.TryGetValue(jobId, out PendingJob job))
                return;

            s_PendingJobs.Remove(jobId);
            job.CancelRegistration.Dispose();

            if (status != 0 || dataPtr == IntPtr.Zero || byteLength <= 0)
            {
                if (status != 0)
                {
                    Debug.LogError($"WebGL Web Worker ASTC operation failed (status={status}).");
                }
                job.CompressTcs?.TrySetResult(null);
                job.DecompressTcs?.TrySetResult(null);
                return;
            }

            if (job.DecompressTcs != null)
            {
                try
                {
                    byte[] output = new byte[byteLength];
                    Marshal.Copy(dataPtr, output, 0, byteLength);
                    job.DecompressTcs.TrySetResult(output);
                }
                catch (Exception ex)
                {
                    job.DecompressTcs.TrySetException(ex);
                }
                return;
            }

            IntPtr owned = Marshal.AllocHGlobal(byteLength);
            try
            {
                unsafe
                {
                    UnsafeUtility.MemCpy((void*)owned, (void*)dataPtr, byteLength);
                }
                job.CompressTcs?.TrySetResult(new AstcRaw(owned, byteLength));
            }
            catch (Exception ex)
            {
                Marshal.FreeHGlobal(owned);
                job.CompressTcs?.TrySetException(ex);
            }
        }

        internal static unsafe Task<AstcRaw> CompressTextureAsync(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<AstcRaw>(cancellationToken);

            if (!s_IsReady || inputTexture == null)
                return Task.FromResult<AstcRaw>(null);

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out _))
                return Task.FromResult<AstcRaw>(null);

            GraphicsFormat format = inputTexture.graphicsFormat;
            int width = inputTexture.width;
            int height = inputTexture.height;
            int mipCount = includeMipmaps ? Math.Max(1, inputTexture.mipmapCount) : 1;

            NativeArray<byte> rawBytes = inputTexture.GetRawTextureData<byte>();
            void* srcPtr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rawBytes);

            return DispatchPixelsAsync(
                srcPtr,
                rawBytes.Length,
                width,
                height,
                mipCount,
                format,
                textureType,
                blockX,
                blockY,
                quality,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                cancellationToken);
        }

        internal static unsafe Task<AstcRaw> CompressRawPixelsAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<AstcRaw>(cancellationToken);

            if (!s_IsReady || pixelPtr == IntPtr.Zero)
                return Task.FromResult<AstcRaw>(null);

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out _))
                return Task.FromResult<AstcRaw>(null);

            return DispatchPixelsAsync(
                (void*)pixelPtr,
                pixelByteLength,
                width,
                height,
                1,
                sourceFormat,
                textureType,
                blockX,
                blockY,
                quality,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                cancellationToken);
        }

        internal static unsafe Task<AstcRaw> CompressSnapshotAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            int mipCount,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<AstcRaw>(cancellationToken);

            if (!s_IsReady || pixelPtr == IntPtr.Zero)
                return Task.FromResult<AstcRaw>(null);

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out _))
                return Task.FromResult<AstcRaw>(null);

            return DispatchPixelsAsync(
                (void*)pixelPtr,
                pixelByteLength,
                width,
                height,
                mipCount,
                sourceFormat,
                textureType,
                blockX,
                blockY,
                quality,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                cancellationToken);
        }

        private static unsafe Task<AstcRaw> DispatchPixelsAsync(
            void* srcPtr,
            int srcByteLength,
            int width,
            int height,
            int mipCount,
            GraphicsFormat format,
            TextureType textureType,
            uint blockX,
            uint blockY,
            CompressQuality quality,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken)
        {
            long totalPixels = 0;
            long expectedOutBytes = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int mw = Math.Max(1, width >> m);
                int mh = Math.Max(1, height >> m);
                totalPixels += (long)mw * mh;
                long bx = (mw + (long)blockX - 1L) / (long)blockX;
                long by = (mh + (long)blockY - 1L) / (long)blockY;
                expectedOutBytes += bx * by * k_BytePerBlock;
            }

            if (totalPixels <= 0 || expectedOutBytes <= 0 || expectedOutBytes > int.MaxValue)
                return Task.FromResult<AstcRaw>(null);

            int dataType;
            int rgbaStride;
            IntPtr rgbaBuffer = IntPtr.Zero;
            bool ownsRgbaBuffer = false;
            int rgbaByteLen;

            try
            {
                if (!PrepareRgba4Buffer(srcPtr, srcByteLength, (int)totalPixels, format, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsRgbaBuffer))
                {
                    return Task.FromResult<AstcRaw>(null);
                }

                astcenc_profile profile = AstcCpuBackend.GetColorProfile(format, textureType);
                CodecFlags flags = AstcCpuBackend.GetFlags(textureType, format, useAlphaWeight, usePerceptual);
                float qualityValue = AstcCpuBackend.GetQualityValue(quality);
                astcenc_swizzle swizzle = AstcCpuBackend.GetSwizzle(format, textureType);

                int jobId = ++s_NextJobId;
                var tcs = new TaskCompletionSource<AstcRaw>();
                var job = new PendingJob { CompressTcs = tcs };

                if (cancellationToken.CanBeCanceled)
                {
                    job.CancelRegistration = cancellationToken.Register(() =>
                    {
                        if (s_PendingJobs.Remove(jobId))
                        {
                            AstcWebWorker_Cancel(jobId);
                            tcs.TrySetCanceled(cancellationToken);
                        }
                    });
                }

                s_PendingJobs[jobId] = job;

                int queued = AstcWebWorker_CompressAsync(
                    jobId,
                    (int)profile,
                    blockX,
                    blockY,
                    qualityValue,
                    (uint)flags,
                    alphaScaleRadius,
                    (int)swizzle.r,
                    (int)swizzle.g,
                    (int)swizzle.b,
                    (int)swizzle.a,
                    (uint)width,
                    (uint)height,
                    (uint)mipCount,
                    dataType,
                    rgbaBuffer,
                    rgbaByteLen,
                    (int)expectedOutBytes);

                if (queued == 0)
                {
                    if (s_PendingJobs.Remove(jobId))
                    {
                        job.CancelRegistration.Dispose();
                    }
                    return Task.FromResult<AstcRaw>(null);
                }

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

        internal static unsafe bool PrepareRgba4Buffer(
            void* srcPtr,
            int srcByteLength,
            int totalPixels,
            GraphicsFormat format,
            out IntPtr rgbaBuffer,
            out int rgbaByteLen,
            out int dataType,
            out int rgbaStride,
            out bool ownsBuffer)
        {
            rgbaBuffer = IntPtr.Zero;
            rgbaByteLen = 0;
            dataType = 0;
            rgbaStride = 4;
            ownsBuffer = false;

            switch (format)
            {
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    rgbaStride = 4;
                    dataType = 0; // ASTCENC_TYPE_U8
                    rgbaByteLen = totalPixels * 4;
                    if (srcByteLength < rgbaByteLen) return false;
                    rgbaBuffer = (IntPtr)srcPtr;
                    return true;

                case GraphicsFormat.R16G16B16A16_SFloat:
                    rgbaStride = 8;
                    dataType = 1; // ASTCENC_TYPE_F16
                    rgbaByteLen = totalPixels * 8;
                    if (srcByteLength < rgbaByteLen) return false;
                    rgbaBuffer = (IntPtr)srcPtr;
                    return true;

                case GraphicsFormat.R32G32B32A32_SFloat:
                    rgbaStride = 16;
                    dataType = 2; // ASTCENC_TYPE_F32
                    rgbaByteLen = totalPixels * 16;
                    if (srcByteLength < rgbaByteLen) return false;
                    rgbaBuffer = (IntPtr)srcPtr;
                    return true;

                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_SRGB:
                    return ExpandToRgba4<R8>(srcPtr, srcByteLength, totalPixels, 4, 0, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_SRGB:
                    return ExpandToRgba4<RG8>(srcPtr, srcByteLength, totalPixels, 4, 0, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.B8G8R8_UNorm:
                case GraphicsFormat.B8G8R8_SRGB:
                    return ExpandToRgba4<RGB8>(srcPtr, srcByteLength, totalPixels, 4, 0, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16_UNorm:
                    return ExpandToRgba4<R16>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16G16_UNorm:
                    return ExpandToRgba4<RG16>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16G16B16_UNorm:
                    return ExpandToRgba4<RGB16>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16G16B16A16_UNorm:
                    return ExpandToRgba4<RGBA16>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16_SFloat:
                    return ExpandToRgba4<R16F>(srcPtr, srcByteLength, totalPixels, 8, 1, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16G16_SFloat:
                    return ExpandToRgba4<RG16F>(srcPtr, srcByteLength, totalPixels, 8, 1, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R16G16B16_SFloat:
                    return ExpandToRgba4<RGB16F>(srcPtr, srcByteLength, totalPixels, 8, 1, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R32_SFloat:
                    return ExpandToRgba4<R32F>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R32G32_SFloat:
                    return ExpandToRgba4<RG32F>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                case GraphicsFormat.R32G32B32_SFloat:
                    return ExpandToRgba4<RGB32F>(srcPtr, srcByteLength, totalPixels, 16, 2, out rgbaBuffer, out rgbaByteLen, out dataType, out rgbaStride, out ownsBuffer);
                default:
                    return false;
            }
        }

        private static unsafe bool ExpandToRgba4<T>(
            void* srcPtr,
            int srcByteLength,
            int totalPixels,
            int dstStride,
            int dstDataType,
            out IntPtr rgbaBuffer,
            out int rgbaByteLen,
            out int dataType,
            out int rgbaStride,
            out bool ownsBuffer) where T : unmanaged, IRawData
        {
            rgbaBuffer = IntPtr.Zero;
            rgbaByteLen = 0;
            dataType = dstDataType;
            rgbaStride = dstStride;
            ownsBuffer = false;

            long requiredSrc = (long)totalPixels * sizeof(T);
            long requiredDst = (long)totalPixels * dstStride;
            if (requiredSrc > srcByteLength || requiredDst <= 0 || requiredDst > int.MaxValue)
                return false;

            IntPtr dst = Marshal.AllocHGlobal((int)requiredDst);
            try
            {
                T* src = (T*)srcPtr;
                byte* outPtr = (byte*)dst;
                for (int i = 0; i < totalPixels; i++)
                {
                    src[i].WriteTo(outPtr);
                    outPtr += dstStride;
                }
                rgbaBuffer = dst;
                rgbaByteLen = (int)requiredDst;
                ownsBuffer = true;
                return true;
            }
            catch
            {
                Marshal.FreeHGlobal(dst);
                throw;
            }
        }

        internal static unsafe Task<byte[]> DecompressAsync(
            IntPtr astcPtr,
            int astcByteLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<byte[]>(cancellationToken);

            if (!s_IsReady || astcPtr == IntPtr.Zero || astcByteLength <= 0 || width <= 0 || height <= 0)
                return Task.FromResult<byte[]>(null);

            if (!Encoder.GetBlockSize(blockSize, out uint blockX, out uint blockY, out _))
                return Task.FromResult<byte[]>(null);

            int bpp = outputBitness == 32 ? 16 : (outputBitness == 16 ? 8 : 4);
            long expectedOutLong = (long)width * height * bpp;
            if (expectedOutLong <= 0 || expectedOutLong > int.MaxValue)
                return Task.FromResult<byte[]>(null);

            astcenc_profile profile = isHdr
                ? astcenc_profile.ASTCENC_PRF_HDR
                : (srgb ? astcenc_profile.ASTCENC_PRF_LDR_SRGB : astcenc_profile.ASTCENC_PRF_LDR);

            int jobId = ++s_NextJobId;
            var tcs = new TaskCompletionSource<byte[]>();
            var job = new PendingJob { DecompressTcs = tcs };

            if (cancellationToken.CanBeCanceled)
            {
                job.CancelRegistration = cancellationToken.Register(() =>
                {
                    if (s_PendingJobs.Remove(jobId))
                    {
                        AstcWebWorker_Cancel(jobId);
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
            }

            s_PendingJobs[jobId] = job;

            int queued = AstcWebWorker_DecompressAsync(
                jobId,
                (int)profile,
                blockX,
                blockY,
                (uint)width,
                (uint)height,
                outputBitness,
                astcPtr,
                astcByteLength,
                (int)expectedOutLong);

            if (queued == 0)
            {
                if (s_PendingJobs.Remove(jobId))
                {
                    job.CancelRegistration.Dispose();
                }
                return Task.FromResult<byte[]>(null);
            }

            return tcs.Task;
        }

        private static string CombineStreamingAssetsUrl(string relativePath)
        {
            string basePath = Application.streamingAssetsPath;
            if (string.IsNullOrEmpty(basePath))
                return "StreamingAssets/" + relativePath;
            return basePath.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        }
#else
        internal static bool IsSupported => false;
        internal static bool IsReady => false;
        internal static bool IsSimdEnabled => false;
        internal static int WorkerCount => 0;

        internal static Task<bool> EnsureInitializedAsync() => Task.FromResult(false);
        internal static Task<bool> InitializeAsync(string workerScriptUrl = null, int maxWorkers = 0) => Task.FromResult(false);
        internal static void Shutdown() { }

        internal static Task<AstcRaw> CompressTextureAsync(
            Texture2D inputTexture,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken) => Task.FromResult<AstcRaw>(null);

        internal static Task<AstcRaw> CompressRawPixelsAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken) => Task.FromResult<AstcRaw>(null);

        internal static Task<AstcRaw> CompressSnapshotAsync(
            IntPtr pixelPtr,
            int pixelByteLength,
            int width,
            int height,
            int mipCount,
            GraphicsFormat sourceFormat,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            CancellationToken cancellationToken) => Task.FromResult<AstcRaw>(null);

        internal static Task<byte[]> DecompressAsync(
            IntPtr astcPtr,
            int astcByteLength,
            int width,
            int height,
            BlockSize blockSize,
            bool srgb,
            bool isHdr,
            int outputBitness,
            CancellationToken cancellationToken) => Task.FromResult<byte[]>(null);
#endif
    }
}
