using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Scheduling priority for <see cref="AstcCompressionQueue"/> jobs.
    /// </summary>
    public enum AstcTaskPriority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }

    /// <summary>
    /// Frame-budgeted priority queue for runtime ASTC compression with pooled CPU <see cref="AstcContext"/>
    /// table sharing. Call <see cref="ProcessFrame"/> once per frame from a MonoBehaviour Update() loop
    /// to spread bursts of texture compressions smoothly across frames without frame-time spikes.
    /// </summary>
    public sealed class AstcCompressionQueue : IDisposable
    {
        private readonly struct ContextPoolKey : IEquatable<ContextPoolKey>
        {
            public readonly GraphicsFormat Format;
            public readonly TextureType TextureType;
            public readonly BlockSize BlockSize;
            public readonly CompressQuality Quality;
            public readonly int ThreadCount;
            public readonly bool UseAlphaWeight;
            public readonly bool UsePerceptual;
            public readonly uint AlphaScaleRadius;

            public ContextPoolKey(GraphicsFormat format, AstcCompressOptions options)
            {
                Format = format;
                TextureType = options.TextureType;
                BlockSize = options.BlockSize;
                Quality = options.Quality;
                ThreadCount = options.ThreadCount;
                UseAlphaWeight = options.UseAlphaWeight;
                UsePerceptual = options.UsePerceptual;
                AlphaScaleRadius = options.AlphaScaleRadius;
            }

            public bool Equals(ContextPoolKey other) =>
                Format == other.Format &&
                TextureType == other.TextureType &&
                BlockSize == other.BlockSize &&
                Quality == other.Quality &&
                ThreadCount == other.ThreadCount &&
                UseAlphaWeight == other.UseAlphaWeight &&
                UsePerceptual == other.UsePerceptual &&
                AlphaScaleRadius == other.AlphaScaleRadius;

            public override bool Equals(object obj) => obj is ContextPoolKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (int)Format;
                    h = (h * 397) ^ (int)TextureType;
                    h = (h * 397) ^ (int)BlockSize;
                    h = (h * 397) ^ (int)Quality;
                    h = (h * 397) ^ ThreadCount;
                    h = (h * 397) ^ (UseAlphaWeight ? 1 : 0);
                    h = (h * 397) ^ (UsePerceptual ? 1 : 0);
                    h = (h * 397) ^ (int)AlphaScaleRadius;
                    return h;
                }
            }
        }

        private sealed class PendingJob
        {
            public Texture Source;
            public AstcCompressOptions Options;
            public AstcTaskPriority Priority;
            public bool OutputTexture;
            public long TexelCost;
            public CancellationToken CancellationToken;
            public TaskCompletionSource<Texture2D> TextureTcs;
            public TaskCompletionSource<AstcRaw> RawTcs;

            public void Cancel()
            {
                TextureTcs?.TrySetCanceled();
                RawTcs?.TrySetCanceled();
            }

            public void SetException(Exception ex)
            {
                TextureTcs?.TrySetException(ex);
                RawTcs?.TrySetException(ex);
            }
        }

        private readonly Queue<PendingJob>[] m_GpuQueues =
        {
            new Queue<PendingJob>(),
            new Queue<PendingJob>(),
            new Queue<PendingJob>()
        };

        private readonly Queue<PendingJob>[] m_CpuQueues =
        {
            new Queue<PendingJob>(),
            new Queue<PendingJob>(),
            new Queue<PendingJob>()
        };

        private readonly object m_PoolLock = new object();
        private readonly Dictionary<ContextPoolKey, Stack<AstcContext>> m_ContextPool = new Dictionary<ContextPoolKey, Stack<AstcContext>>();
        private readonly Dictionary<BlockSize, AstcContext> m_ParentContexts = new Dictionary<BlockSize, AstcContext>();

        private int m_ActiveCpuCount;
        private int m_InFlightContextCount;
        private bool m_Disposed;

        /// <summary>
        /// Maximum number of GPU texels dispatched per <see cref="ProcessFrame"/> call (default: 1024 x 1024).
        /// At least one pending GPU job is always dispatched per frame even if it exceeds this budget.
        /// </summary>
        public long MaxGpuTexelsPerFrame { get; set; } = 1024L * 1024L;

        /// <summary>
        /// Maximum number of concurrent background CPU compression jobs (default: half of logical cores, minimum 1).
        /// </summary>
        public int MaxConcurrentCpuTasks { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

        public int PendingGpuCount => m_GpuQueues[0].Count + m_GpuQueues[1].Count + m_GpuQueues[2].Count;
        public int PendingCpuCount => m_CpuQueues[0].Count + m_CpuQueues[1].Count + m_CpuQueues[2].Count;
        public int ActiveCpuCount => Volatile.Read(ref m_ActiveCpuCount);

        /// <summary>
        /// Enqueues a texture to be compressed into a new ASTC <see cref="Texture2D"/> on a future <see cref="ProcessFrame"/> tick.
        /// </summary>
        public Task<Texture2D> EnqueueToTexture(
            Texture source,
            AstcCompressOptions options,
            AstcTaskPriority priority = AstcTaskPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var tcs = new TaskCompletionSource<Texture2D>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.SetCanceled();
                return tcs.Task;
            }
            if (source == null)
            {
                tcs.SetResult(null);
                return tcs.Task;
            }

            EnqueueJob(new PendingJob
            {
                Source = source,
                Options = options,
                Priority = priority,
                OutputTexture = true,
                TexelCost = Math.Max(1L, (long)source.width * source.height),
                CancellationToken = cancellationToken,
                TextureTcs = tcs
            });
            return tcs.Task;
        }

        /// <summary>
        /// Enqueues a texture to be compressed into raw ASTC bytes (<see cref="AstcRaw"/>) on a future <see cref="ProcessFrame"/> tick.
        /// Caller owns and must Dispose() the returned <see cref="AstcRaw"/>.
        /// </summary>
        public Task<AstcRaw> EnqueueToRaw(
            Texture source,
            AstcCompressOptions options,
            AstcTaskPriority priority = AstcTaskPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var tcs = new TaskCompletionSource<AstcRaw>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.SetCanceled();
                return tcs.Task;
            }
            if (source == null)
            {
                tcs.SetResult(null);
                return tcs.Task;
            }

            EnqueueJob(new PendingJob
            {
                Source = source,
                Options = options,
                Priority = priority,
                OutputTexture = false,
                TexelCost = Math.Max(1L, (long)source.width * source.height),
                CancellationToken = cancellationToken,
                RawTcs = tcs
            });
            return tcs.Task;
        }

        /// <summary>
        /// Processes queued GPU jobs up to <see cref="MaxGpuTexelsPerFrame"/> and starts queued CPU jobs
        /// up to <see cref="MaxConcurrentCpuTasks"/>. Must be called from the Unity main thread.
        /// </summary>
        public void ProcessFrame()
        {
            if (m_Disposed)
                return;

            // 1. Dispatch GPU jobs within the frame texel budget
            long remainingBudget = Math.Max(1L, MaxGpuTexelsPerFrame);
            bool dispatchedAnyGpu = false;

            while (TryPeekHighestPriority(m_GpuQueues, out PendingJob nextGpu))
            {
                if (nextGpu.CancellationToken.IsCancellationRequested)
                {
                    DequeueHighestPriority(m_GpuQueues);
                    nextGpu.Cancel();
                    continue;
                }

                if (dispatchedAnyGpu && nextGpu.TexelCost > remainingBudget)
                    break;

                DequeueHighestPriority(m_GpuQueues);
                dispatchedAnyGpu = true;
                remainingBudget -= nextGpu.TexelCost;
                RunGpuJob(nextGpu);
            }

            // 2. Dispatch CPU jobs up to MaxConcurrentCpuTasks
            int maxCpu = Math.Max(1, MaxConcurrentCpuTasks);
            while (Volatile.Read(ref m_ActiveCpuCount) < maxCpu && TryPeekHighestPriority(m_CpuQueues, out PendingJob nextCpu))
            {
                DequeueHighestPriority(m_CpuQueues);
                if (nextCpu.CancellationToken.IsCancellationRequested)
                {
                    nextCpu.Cancel();
                    continue;
                }

                RunCpuJob(nextCpu);
            }
        }

        /// <summary>
        /// Cancels all queued jobs that have not yet started executing.
        /// </summary>
        public void CancelAllPending()
        {
            DrainAndCancel(m_GpuQueues);
            DrainAndCancel(m_CpuQueues);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Disposed = true;
            CancelAllPending();

            lock (m_PoolLock)
            {
                foreach (var stack in m_ContextPool.Values)
                {
                    while (stack.Count > 0)
                    {
                        stack.Pop().Dispose();
                    }
                }
                m_ContextPool.Clear();

                // astc-encoder 5.x parent contexts hold shared read-only tables referenced by child contexts.
                // Only free parent contexts once every in-flight child context has been disposed.
                if (m_InFlightContextCount == 0)
                {
                    DisposeParentContextsLocked();
                }
            }
        }

        private void DisposeParentContextsLocked()
        {
            foreach (var parent in m_ParentContexts.Values)
            {
                parent.Dispose();
            }
            m_ParentContexts.Clear();
        }

        private void EnqueueJob(PendingJob job)
        {
            bool useGpu = job.Options.Backend == EncodeBackend.Gpu ||
                          (job.Options.Backend == EncodeBackend.Auto && Encoder.IsGpuSupported);
            int bucket = ClampPriority(job.Priority);
            if (useGpu)
                m_GpuQueues[bucket].Enqueue(job);
            else
                m_CpuQueues[bucket].Enqueue(job);
        }

        private async void RunGpuJob(PendingJob job)
        {
            try
            {
                if (job.Source == null)
                {
                    job.TextureTcs?.TrySetResult(null);
                    job.RawTcs?.TrySetResult(null);
                    return;
                }

                if (job.OutputTexture)
                {
                    Texture2D tex = await job.Source.CompressToTextureAsync(job.Options, job.CancellationToken);
                    if (!job.TextureTcs.TrySetResult(tex) && tex != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tex);
                    }
                }
                else
                {
                    AstcRaw raw = await job.Source.CompressTextureAsync(job.Options, job.CancellationToken);
                    if (!job.RawTcs.TrySetResult(raw) && raw != null)
                    {
                        raw.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                job.Cancel();
            }
            catch (Exception ex)
            {
                job.SetException(ex);
            }
        }

        private async void RunCpuJob(PendingJob job)
        {
            if (!(job.Source is Texture2D tex2d) || tex2d == null)
            {
                job.TextureTcs?.TrySetResult(null);
                job.RawTcs?.TrySetResult(null);
                return;
            }

            // Capture texture metadata on the main thread before awaiting so destroying tex2d mid-flight is safe.
            GraphicsFormat srcFormat = tex2d.graphicsFormat;
            int width = tex2d.width;
            int height = tex2d.height;
            string texName = tex2d.name;

            Interlocked.Increment(ref m_ActiveCpuCount);
            AstcContext rented = null;
            var key = new ContextPoolKey(srcFormat, job.Options);

            try
            {
                rented = RentContext(key, job.Options);
                if (rented == null)
                {
                    job.TextureTcs?.TrySetResult(null);
                    job.RawTcs?.TrySetResult(null);
                    return;
                }

                AstcRaw raw = await AstcCpuBackend.CompressTextureWithContextAsync(tex2d, rented, job.Options.TextureType, job.Options.IncludeMipmaps, job.CancellationToken);
                if (!job.OutputTexture)
                {
                    if (!job.RawTcs.TrySetResult(raw) && raw != null)
                    {
                        raw.Dispose();
                    }
                    return;
                }

                if (raw == null)
                {
                    job.TextureTcs.TrySetResult(null);
                    return;
                }

                using (raw)
                {
                    GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(srcFormat, job.Options.BlockSize, job.Options.TextureType);
                    if (!Encoder.CanCreateAstcTexture(targetFormat))
                    {
                        Debug.LogError($"ASTC texture format '{targetFormat}' is not supported on this device. Use EnqueueToRaw instead.");
                        job.TextureTcs.TrySetResult(null);
                        return;
                    }

                    int mipCount = AstcKtxContainer.InferMipCount(raw.Length, width, height, job.Options.BlockSize);
                    TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
                    var dst = new Texture2D(width, height, targetFormat, mipCount, flags);
                    try
                    {
                        dst.name = texName;
                        dst.LoadRawTextureData(raw.Data, raw.Length);
                        dst.Apply(false, job.Options.MakeNoLongerReadable);
                        if (!job.TextureTcs.TrySetResult(dst))
                        {
                            UnityEngine.Object.DestroyImmediate(dst);
                        }
                    }
                    catch
                    {
                        UnityEngine.Object.DestroyImmediate(dst);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                job.Cancel();
            }
            catch (Exception ex)
            {
                job.SetException(ex);
            }
            finally
            {
                ReturnContext(key, rented);
                Interlocked.Decrement(ref m_ActiveCpuCount);
            }
        }

        private AstcContext RentContext(ContextPoolKey key, AstcCompressOptions options)
        {
            lock (m_PoolLock)
            {
                if (m_Disposed)
                    return null;

                if (m_ContextPool.TryGetValue(key, out Stack<AstcContext> stack) && stack.Count > 0)
                {
                    m_InFlightContextCount++;
                    return stack.Pop();
                }

                // Lazily create a parent context per BlockSize to share read-only astcenc 5.x lookup tables
                if (!m_ParentContexts.TryGetValue(key.BlockSize, out AstcContext parent))
                {
                    parent = AstcContext.Create(
                        key.Format,
                        options.TextureType,
                        options.BlockSize,
                        options.Quality,
                        options.ThreadCount,
                        options.UseAlphaWeight,
                        options.UsePerceptual,
                        options.AlphaScaleRadius);
                    if (parent != null)
                        m_ParentContexts[key.BlockSize] = parent;
                }

                AstcContext child = AstcContext.Create(
                    key.Format,
                    options.TextureType,
                    options.BlockSize,
                    options.Quality,
                    options.ThreadCount,
                    options.UseAlphaWeight,
                    options.UsePerceptual,
                    options.AlphaScaleRadius,
                    parent);
                if (child != null)
                    m_InFlightContextCount++;
                return child;
            }
        }

        private void ReturnContext(ContextPoolKey key, AstcContext ctx)
        {
            if (ctx == null)
                return;

            lock (m_PoolLock)
            {
                if (m_Disposed)
                {
                    ctx.Dispose();
                    m_InFlightContextCount--;
                    if (m_InFlightContextCount <= 0)
                    {
                        m_InFlightContextCount = 0;
                        DisposeParentContextsLocked();
                    }
                    return;
                }

                m_InFlightContextCount--;

                if (!m_ContextPool.TryGetValue(key, out Stack<AstcContext> stack))
                {
                    stack = new Stack<AstcContext>();
                    m_ContextPool[key] = stack;
                }

                if (stack.Count < 4)
                    stack.Push(ctx);
                else
                    ctx.Dispose();
            }
        }

        private static int ClampPriority(AstcTaskPriority p)
        {
            int v = (int)p;
            return v < 0 ? 0 : (v > 2 ? 2 : v);
        }

        private static bool TryPeekHighestPriority(Queue<PendingJob>[] queues, out PendingJob job)
        {
            for (int i = 2; i >= 0; i--)
            {
                if (queues[i].Count > 0)
                {
                    job = queues[i].Peek();
                    return true;
                }
            }
            job = null;
            return false;
        }

        private static PendingJob DequeueHighestPriority(Queue<PendingJob>[] queues)
        {
            for (int i = 2; i >= 0; i--)
            {
                if (queues[i].Count > 0)
                    return queues[i].Dequeue();
            }
            return null;
        }

        private static void DrainAndCancel(Queue<PendingJob>[] queues)
        {
            for (int i = 0; i < queues.Length; i++)
            {
                while (queues[i].Count > 0)
                {
                    queues[i].Dequeue().Cancel();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(AstcCompressionQueue));
        }
    }
}
