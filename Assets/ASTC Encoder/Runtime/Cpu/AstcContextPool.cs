using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Thread-safe static pool of reusable native <see cref="AstcContext"/> instances (both compressor and decompressor)
    /// with astcenc 5.x parentContext read-only lookup-table sharing and safe deferred disposal.
    /// Eliminates repeated astcenc_context_alloc / free overhead across all static CPU Encoder entry points.
    /// </summary>
    internal static class AstcContextPool
    {
        private const int k_MaxCachedPerKey = 4;

        internal readonly struct CompressorKey : IEquatable<CompressorKey>
        {
            public readonly GraphicsFormat Format;
            public readonly TextureType TextureType;
            public readonly BlockSize BlockSize;
            public readonly CompressQuality Quality;
            public readonly int ThreadCount;
            public readonly bool UseAlphaWeight;
            public readonly bool UsePerceptual;
            public readonly uint AlphaScaleRadius;

            public CompressorKey(
                GraphicsFormat format,
                TextureType textureType,
                BlockSize blockSize,
                CompressQuality quality,
                int threadCount,
                bool useAlphaWeight,
                bool usePerceptual,
                uint alphaScaleRadius)
            {
                Format = format;
                TextureType = textureType;
                BlockSize = blockSize;
                Quality = quality;
                ThreadCount = threadCount;
                UseAlphaWeight = useAlphaWeight;
                UsePerceptual = usePerceptual;
                AlphaScaleRadius = alphaScaleRadius;
            }

            public bool Equals(CompressorKey other) =>
                Format == other.Format &&
                TextureType == other.TextureType &&
                BlockSize == other.BlockSize &&
                Quality == other.Quality &&
                ThreadCount == other.ThreadCount &&
                UseAlphaWeight == other.UseAlphaWeight &&
                UsePerceptual == other.UsePerceptual &&
                AlphaScaleRadius == other.AlphaScaleRadius;

            public override bool Equals(object obj) => obj is CompressorKey other && Equals(other);

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

        internal readonly struct DecompressorKey : IEquatable<DecompressorKey>
        {
            public readonly BlockSize BlockSize;
            public readonly bool Srgb;
            public readonly bool IsHdr;
            public readonly int ThreadCount;

            public DecompressorKey(BlockSize blockSize, bool srgb, bool isHdr, int threadCount)
            {
                BlockSize = blockSize;
                Srgb = srgb;
                IsHdr = isHdr;
                ThreadCount = threadCount;
            }

            public bool Equals(DecompressorKey other) =>
                BlockSize == other.BlockSize &&
                Srgb == other.Srgb &&
                IsHdr == other.IsHdr &&
                ThreadCount == other.ThreadCount;

            public override bool Equals(object obj) => obj is DecompressorKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (int)BlockSize;
                    h = (h * 397) ^ (Srgb ? 1 : 0);
                    h = (h * 397) ^ (IsHdr ? 1 : 0);
                    h = (h * 397) ^ ThreadCount;
                    return h;
                }
            }
        }

        internal struct CompressorLease : IDisposable
        {
            private CompressorKey m_Key;
            private AstcContext m_Context;
            private int m_Generation;

            public AstcContext Context => m_Context;

            internal CompressorLease(CompressorKey key, AstcContext context, int generation)
            {
                m_Key = key;
                m_Context = context;
                m_Generation = generation;
            }

            public void Dispose()
            {
                AstcContext ctx = m_Context;
                m_Context = null;
                if (ctx != null)
                {
                    ReturnCompressor(m_Key, ctx, m_Generation);
                }
            }
        }

        internal struct DecompressorLease : IDisposable
        {
            private DecompressorKey m_Key;
            private AstcContext m_Context;
            private int m_Generation;

            public AstcContext Context => m_Context;

            internal DecompressorLease(DecompressorKey key, AstcContext context, int generation)
            {
                m_Key = key;
                m_Context = context;
                m_Generation = generation;
            }

            public void Dispose()
            {
                AstcContext ctx = m_Context;
                m_Context = null;
                if (ctx != null)
                {
                    ReturnDecompressor(m_Key, ctx, m_Generation);
                }
            }
        }

        private static readonly object s_Lock = new object();
        private static readonly Dictionary<CompressorKey, Stack<AstcContext>> s_Compressors = new Dictionary<CompressorKey, Stack<AstcContext>>();
        private static readonly Dictionary<DecompressorKey, Stack<AstcContext>> s_Decompressors = new Dictionary<DecompressorKey, Stack<AstcContext>>();
        private static readonly Dictionary<CompressorKey, AstcContext> s_Parents = new Dictionary<CompressorKey, AstcContext>();
        private static readonly List<AstcContext> s_RetiredParents = new List<AstcContext>();
        private static int s_Generation;
        private static int s_InFlightCompressors;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ReleaseAll();
            Application.quitting -= ReleaseAll;
            Application.quitting += ReleaseAll;
        }

        internal static CompressorLease RentCompressor(
            GraphicsFormat format,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            int threadCount,
            bool useAlphaWeight,
            bool usePerceptual,
            uint alphaScaleRadius,
            bool useUnityJobSystem = false)
        {
            int resolvedThreads = AstcContext.ResolveThreadCount(threadCount);
            var key = new CompressorKey(format, textureType, blockSize, quality, resolvedThreads, useAlphaWeight, usePerceptual, alphaScaleRadius);
            var parentKey = new CompressorKey(format, textureType, blockSize, quality, 1, useAlphaWeight, usePerceptual, alphaScaleRadius);
            lock (s_Lock)
            {
                int gen = s_Generation;
                if (s_Compressors.TryGetValue(key, out Stack<AstcContext> stack) && stack.Count > 0)
                {
                    AstcContext pooled = stack.Pop();
                    pooled.UseUnityJobSystem = useUnityJobSystem;
                    s_InFlightCompressors++;
                    return new CompressorLease(key, pooled, gen);
                }

                if (!s_Parents.TryGetValue(parentKey, out AstcContext parent))
                {
                    parent = AstcContext.Create(format, textureType, blockSize, quality, 1, useAlphaWeight, usePerceptual, alphaScaleRadius);
                    if (parent != null)
                        s_Parents[parentKey] = parent;
                }

                AstcContext created = AstcContext.Create(
                    format,
                    textureType,
                    blockSize,
                    quality,
                    resolvedThreads,
                    useAlphaWeight,
                    usePerceptual,
                    alphaScaleRadius,
                    parent,
                    useUnityJobSystem);
                if (created != null)
                    s_InFlightCompressors++;
                return new CompressorLease(key, created, gen);
            }
        }

        internal static DecompressorLease RentDecompressor(BlockSize blockSize, bool srgb, bool isHdr, int threadCount)
        {
            int resolvedThreads = AstcContext.ResolveThreadCount(threadCount);
            var key = new DecompressorKey(blockSize, srgb, isHdr, resolvedThreads);
            lock (s_Lock)
            {
                int gen = s_Generation;
                if (s_Decompressors.TryGetValue(key, out Stack<AstcContext> stack) && stack.Count > 0)
                {
                    return new DecompressorLease(key, stack.Pop(), gen);
                }

                AstcContext created = AstcContext.CreateDecompressor(blockSize, srgb, isHdr, resolvedThreads);
                return new DecompressorLease(key, created, gen);
            }
        }

        private static void ReturnCompressor(CompressorKey key, AstcContext ctx, int generation)
        {
            lock (s_Lock)
            {
                s_InFlightCompressors = Math.Max(0, s_InFlightCompressors - 1);

                if (generation != s_Generation)
                {
                    ctx.Dispose();
                    if (s_InFlightCompressors == 0 && s_RetiredParents.Count > 0)
                    {
                        for (int i = 0; i < s_RetiredParents.Count; i++)
                            s_RetiredParents[i].Dispose();
                        s_RetiredParents.Clear();
                    }
                    return;
                }

                if (!s_Compressors.TryGetValue(key, out Stack<AstcContext> stack))
                {
                    stack = new Stack<AstcContext>();
                    s_Compressors[key] = stack;
                }

                if (stack.Count < k_MaxCachedPerKey)
                    stack.Push(ctx);
                else
                    ctx.Dispose();
            }
        }

        private static void ReturnDecompressor(DecompressorKey key, AstcContext ctx, int generation)
        {
            lock (s_Lock)
            {
                if (generation != s_Generation)
                {
                    ctx.Dispose();
                    return;
                }

                if (!s_Decompressors.TryGetValue(key, out Stack<AstcContext> stack))
                {
                    stack = new Stack<AstcContext>();
                    s_Decompressors[key] = stack;
                }

                if (stack.Count < k_MaxCachedPerKey)
                    stack.Push(ctx);
                else
                    ctx.Dispose();
            }
        }

        internal static void ReleaseAll()
        {
            lock (s_Lock)
            {
                s_Generation++;

                foreach (var stack in s_Compressors.Values)
                {
                    while (stack.Count > 0)
                        stack.Pop().Dispose();
                }
                s_Compressors.Clear();

                foreach (var stack in s_Decompressors.Values)
                {
                    while (stack.Count > 0)
                        stack.Pop().Dispose();
                }
                s_Decompressors.Clear();

                if (s_InFlightCompressors == 0)
                {
                    foreach (var p in s_Parents.Values)
                        p.Dispose();
                    for (int i = 0; i < s_RetiredParents.Count; i++)
                        s_RetiredParents[i].Dispose();
                    s_RetiredParents.Clear();
                }
                else
                {
                    foreach (var p in s_Parents.Values)
                        s_RetiredParents.Add(p);
                }
                s_Parents.Clear();
            }
        }
    }
}
