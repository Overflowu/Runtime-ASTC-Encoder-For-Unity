using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// GPU-readback fallback bridge for platforms without Compute Shader support (such as WebGL2 and Tuanjie MiniGames).
    /// Blits RenderTextures, non-readable Texture2Ds, Cubemaps, and Texture2DArrays into temporary RenderTargets,
    /// generates mipmaps via box downsampling when requested, reads back RGBA pixels, and delegates encoding to
    /// <see cref="AstcCpuBackend"/> (including Web Workers / pthreads when available).
    /// </summary>
    internal static class AstcReadbackBridge
    {
        private const string ShaderResourceName = "AstcReadbackBlit";

        private static readonly int s_MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int s_CubeTexId = Shader.PropertyToID("_CubeTex");
        private static readonly int s_ArrayTexId = Shader.PropertyToID("_ArrayTex");
        private static readonly int s_SourceMipId = Shader.PropertyToID("_SourceMip");
        private static readonly int s_SliceId = Shader.PropertyToID("_Slice");
        private static readonly int s_FaceId = Shader.PropertyToID("_Face");
        private static readonly int s_TexelSizeId = Shader.PropertyToID("_TexelSize");

        private static Shader s_BlitShader;
        private static Material s_BlitMaterial;
        private static bool s_ShaderChecked;

        /// <summary>
        /// Test hook allowing EditMode tests (where Compute Shaders are available) to exercise the readback path.
        /// </summary>
        internal static bool ForceEnableForTests { get; set; }

        internal static bool IsSupported
        {
            get
            {
                if (!AstcCpuBackend.IsSupported || !SystemInfo.supportsRenderTextures)
                    return false;

                if (!ForceEnableForTests && AstcGpuBackend.IsSupported)
                    return false;

                return EnsureMaterial();
            }
        }

        internal static void Preload()
        {
            EnsureMaterial();
        }

        internal static void ReleaseCache()
        {
            if (s_BlitMaterial != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(s_BlitMaterial);
                else
                    UnityEngine.Object.DestroyImmediate(s_BlitMaterial);
                s_BlitMaterial = null;
            }
            s_BlitShader = null;
            s_ShaderChecked = false;
        }

        internal static bool IsInputSupported(Texture texture)
        {
            if (texture == null)
                return false;

            if (texture.dimension != TextureDimension.Tex2D)
                return false;

            if (texture is RenderTexture rt && rt.antiAliasing > 1)
                return false;

            return texture is Texture2D || texture is RenderTexture;
        }

        internal static bool IsSliceableInputSupported(Texture texture)
        {
            if (texture == null)
                return false;

            return texture.dimension == TextureDimension.Cube || texture.dimension == TextureDimension.Tex2DArray;
        }

        private static bool EnsureMaterial()
        {
            if (s_BlitMaterial != null)
                return true;

            if (s_ShaderChecked && s_BlitShader == null)
                return false;

            s_ShaderChecked = true;
            s_BlitShader = Resources.Load<Shader>(ShaderResourceName);
            if (s_BlitShader == null || !s_BlitShader.isSupported)
                return false;

            s_BlitMaterial = new Material(s_BlitShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return true;
        }

        internal sealed class ReadbackSnapshot : IDisposable
        {
            public IntPtr Buffer;
            public int ByteLength;
            public GraphicsFormat Format;
            public int Width;
            public int Height;
            public int MipCount;

            public void Dispose()
            {
                IntPtr ptr = Interlocked.Exchange(ref Buffer, IntPtr.Zero);
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        internal static unsafe ReadbackSnapshot CaptureSlice(
            Texture src,
            int faceOrSlice,
            TextureDimension dimension,
            TextureType textureType,
            bool includeMipmaps,
            bool generateMipmaps)
        {
            if (src == null || !EnsureMaterial())
                return null;

            int width = src.width;
            int height = src.height;
            if (width <= 0 || height <= 0)
                return null;

            GraphicsFormat srcFormat = src.graphicsFormat;
            bool isHdr = AstcGpuBackend.IsHdrFormat(srcFormat) && textureType != TextureType.NormalMap;
            bool isSrgb = textureType != TextureType.NormalMap && !isHdr && GraphicsFormatUtility.IsSRGBFormat(srcFormat);

            bool useHalf = isHdr && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);
            if (isHdr && !useHalf)
            {
                Debug.LogWarning("[ASTC Encoder] ARGBHalf RenderTexture is not supported on this Web/device context; falling back to LDR ARGB32 readback.");
            }

            RenderTextureFormat rtFormat = useHalf ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            RenderTextureReadWrite rtRw = isSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            TextureFormat texFormat = useHalf ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            GraphicsFormat capturedFormat = useHalf
                ? GraphicsFormat.R16G16B16A16_SFloat
                : (isSrgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm);
            int bpp = useHalf ? 8 : 4;

            int maxMips = ComputeMaxMipCount(width, height);
            int mipCount;
            if (!includeMipmaps)
            {
                mipCount = 1;
            }
            else if (generateMipmaps)
            {
                mipCount = maxMips;
            }
            else
            {
                mipCount = Math.Max(1, Math.Min(src.mipmapCount, maxMips));
            }

            long totalBytesLong = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int mw = Math.Max(1, width >> m);
                int mh = Math.Max(1, height >> m);
                totalBytesLong += (long)mw * mh * bpp;
            }

            if (totalBytesLong <= 0 || totalBytesLong > int.MaxValue)
                return null;

            int totalBytes = (int)totalBytesLong;
            IntPtr buffer = Marshal.AllocHGlobal(totalBytes);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevMipRt = null;
            Texture2D scratchTex = null;

            try
            {
                scratchTex = new Texture2D(width, height, texFormat, false, !isSrgb)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                long offset = 0;
                for (int m = 0; m < mipCount; m++)
                {
                    int mw = Math.Max(1, width >> m);
                    int mh = Math.Max(1, height >> m);

                    RenderTexture currentRt = RenderTexture.GetTemporary(mw, mh, 0, rtFormat, rtRw);
                    currentRt.filterMode = FilterMode.Bilinear;
                    currentRt.wrapMode = TextureWrapMode.Clamp;

                    if (generateMipmaps && m > 0 && prevMipRt != null)
                    {
                        s_BlitMaterial.SetTexture(s_MainTexId, prevMipRt);
                        s_BlitMaterial.SetFloat(s_SourceMipId, 0f);
                        s_BlitMaterial.SetVector(s_TexelSizeId, new Vector4(1f / prevMipRt.width, 1f / prevMipRt.height, prevMipRt.width, prevMipRt.height));
                        Graphics.Blit(prevMipRt, currentRt, s_BlitMaterial, 1);
                    }
                    else
                    {
                        s_BlitMaterial.SetFloat(s_SourceMipId, m);
                        if (dimension == TextureDimension.Cube)
                        {
                            s_BlitMaterial.SetTexture(s_CubeTexId, src);
                            s_BlitMaterial.SetFloat(s_FaceId, faceOrSlice);
                            Graphics.Blit(null, currentRt, s_BlitMaterial, 2);
                        }
                        else if (dimension == TextureDimension.Tex2DArray)
                        {
                            s_BlitMaterial.SetTexture(s_ArrayTexId, src);
                            s_BlitMaterial.SetFloat(s_SliceId, faceOrSlice);
                            Graphics.Blit(null, currentRt, s_BlitMaterial, 3);
                        }
                        else
                        {
                            s_BlitMaterial.SetTexture(s_MainTexId, src);
                            Graphics.Blit(src, currentRt, s_BlitMaterial, 0);
                        }
                    }

                    if (prevMipRt != null)
                    {
                        RenderTexture.ReleaseTemporary(prevMipRt);
                        prevMipRt = null;
                    }

                    RenderTexture.active = currentRt;
                    if (scratchTex.width != mw || scratchTex.height != mh)
                    {
                        scratchTex.Reinitialize(mw, mh);
                    }
                    scratchTex.ReadPixels(new Rect(0, 0, mw, mh), 0, 0, false);

                    NativeArray<byte> raw = scratchTex.GetRawTextureData<byte>();
                    long expectedMipBytes = (long)mw * mh * bpp;
                    if (raw.Length < expectedMipBytes)
                    {
                        RenderTexture.ReleaseTemporary(currentRt);
                        Marshal.FreeHGlobal(buffer);
                        return null;
                    }

                    void* rawPtr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw);
                    UnsafeUtility.MemCpy((byte*)buffer + offset, rawPtr, expectedMipBytes);
                    offset += expectedMipBytes;

                    if (generateMipmaps && m + 1 < mipCount)
                    {
                        prevMipRt = currentRt;
                    }
                    else
                    {
                        RenderTexture.ReleaseTemporary(currentRt);
                    }
                }

                return new ReadbackSnapshot
                {
                    Buffer = buffer,
                    ByteLength = totalBytes,
                    Format = capturedFormat,
                    Width = width,
                    Height = height,
                    MipCount = mipCount
                };
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (prevMipRt != null)
                    RenderTexture.ReleaseTemporary(prevMipRt);
                if (scratchTex != null)
                {
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(scratchTex);
                    else
                        UnityEngine.Object.DestroyImmediate(scratchTex);
                }
            }
        }

        internal static Task<ReadbackSnapshot> CaptureSliceAsync(
            Texture src,
            int faceOrSlice,
            TextureDimension dimension,
            TextureType textureType,
            bool includeMipmaps,
            bool generateMipmaps,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<ReadbackSnapshot>(cancellationToken);

            if (src == null || !EnsureMaterial())
                return Task.FromResult<ReadbackSnapshot>(null);

            // In EditMode (not playing), coroutines don't run; capture immediately on caller thread.
            if (!Application.isPlaying)
            {
                return Task.FromResult(CaptureSlice(src, faceOrSlice, dimension, textureType, includeMipmaps, generateMipmaps));
            }

            var tcs = new TaskCompletionSource<ReadbackSnapshot>();
            CancellationTokenRegistration reg = default;
            if (cancellationToken.CanBeCanceled)
            {
                reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            AstcMainThreadPump.EnqueueEndOfFrame(() =>
            {
                reg.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                if (src == null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                try
                {
                    ReadbackSnapshot snap = CaptureSlice(src, faceOrSlice, dimension, textureType, includeMipmaps, generateMipmaps);
                    tcs.TrySetResult(snap);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        internal static AstcRaw CompressTextureSync(
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
            bool useUnityJobSystem = false)
        {
            using ReadbackSnapshot snap = CaptureSlice(src, 0, src.dimension, textureType, includeMipmaps, generateMipmaps);
            if (snap == null)
                return null;

            return AstcCpuBackend.CompressSnapshotSync(
                snap.Buffer,
                snap.ByteLength,
                snap.Width,
                snap.Height,
                snap.MipCount,
                snap.Format,
                textureType,
                blockSize,
                quality,
                threadCount,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                useUnityJobSystem);
        }

        internal static async Task<AstcRaw> CompressTextureAsync(
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
            uint alphaScaleRadius = 1u)
        {
            using ReadbackSnapshot snap = await CaptureSliceAsync(
                src, 0, src.dimension, textureType, includeMipmaps, generateMipmaps, cancellationToken);
            if (snap == null)
                return null;

            return await AstcCpuBackend.CompressSnapshotAsync(
                snap.Buffer,
                snap.ByteLength,
                snap.Width,
                snap.Height,
                snap.MipCount,
                snap.Format,
                textureType,
                blockSize,
                quality,
                threadCount,
                useAlphaWeight,
                usePerceptual,
                alphaScaleRadius,
                cancellationToken);
        }

        internal static Texture2D CompressToTextureSync(
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
            bool useUnityJobSystem = false)
        {
            if (source == null)
                return null;

            GraphicsFormat srcFormat = source.graphicsFormat;
            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(srcFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(targetFormat))
            {
                Debug.LogError($"ASTC texture format '{targetFormat}' is not supported on this device.");
                return null;
            }

            using AstcRaw raw = CompressTextureSync(
                source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps,
                threadCount, usePerceptual, alphaScaleRadius, useUnityJobSystem);
            if (raw == null)
                return null;

            return CreateAstcTextureFromRaw(raw, source.width, source.height, targetFormat, blockSize, source.name);
        }

        internal static async Task<Texture2D> CompressToTextureAsync(
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
            uint alphaScaleRadius = 1u)
        {
            if (source == null)
                return null;

            int width = source.width;
            int height = source.height;
            string texName = source.name;
            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(source.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(targetFormat))
            {
                Debug.LogError($"ASTC texture format '{targetFormat}' is not supported on this device.");
                return null;
            }

            using AstcRaw raw = await CompressTextureAsync(
                source, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, cancellationToken,
                generateMipmaps, threadCount, usePerceptual, alphaScaleRadius);
            if (raw == null)
                return null;

            return CreateAstcTextureFromRaw(raw, width, height, targetFormat, blockSize, texName);
        }

        internal static bool CompressInPlace(
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
            bool useUnityJobSystem = false)
        {
            if (inputTexture == null)
                return false;

            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(inputTexture.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(targetFormat))
                return false;

            bool includeMipmaps = inputTexture.mipmapCount > 1 || generateMipmaps;
            using AstcRaw raw = CompressTextureSync(
                inputTexture, textureType, blockSize, quality, includeMipmaps, useAlphaWeight, generateMipmaps,
                threadCount, usePerceptual, alphaScaleRadius, useUnityJobSystem);
            if (raw == null)
                return false;

            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, inputTexture.width, inputTexture.height, blockSize);
#if UNITY_2021_2_OR_NEWER
            if (!inputTexture.Reinitialize(inputTexture.width, inputTexture.height, targetFormat, mipCount > 1))
                return false;
#else
            if (!inputTexture.Resize(inputTexture.width, inputTexture.height, targetFormat, mipCount > 1))
                return false;
#endif
            inputTexture.LoadRawTextureData(raw.Data, raw.Length);
            inputTexture.Apply(false, makeNoLongerReadable);
            if (!makeNoLongerReadable)
                AstcAtlasShadowUpdater.Register(inputTexture, raw, blockSize);
            return true;
        }

        internal static Cubemap CompressCubemap(
            Cubemap source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (source == null || !IsSupported)
                return null;

            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(source.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(targetFormat))
            {
                Debug.LogError($"ASTC texture format '{targetFormat}' is not supported on this device.");
                return null;
            }

            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var dst = new Cubemap(source.width, targetFormat, flags, mipCount)
            {
                name = source.name
            };

            try
            {
                for (int face = 0; face < 6; face++)
                {
                    using ReadbackSnapshot snap = CaptureSlice(source, face, TextureDimension.Cube, textureType, includeMipmaps, generateMipmaps: false);
                    if (snap == null)
                        throw new InvalidOperationException($"Failed to read back cubemap face {face}.");

                    using AstcRaw raw = AstcCpuBackend.CompressSnapshotSync(
                        snap.Buffer, snap.ByteLength, snap.Width, snap.Height, snap.MipCount,
                        snap.Format, textureType, blockSize, quality, 1, useAlphaWeight, false, 1u, false);
                    if (raw == null)
                        throw new InvalidOperationException($"Failed to encode cubemap face {face}.");

                    UploadMipSlices(raw, source.width, source.height, snap.MipCount, blockSize, (sliceBytes, mip) =>
                    {
                        dst.SetPixelData(sliceBytes, mip, (CubemapFace)face);
                    });
                }

                dst.Apply(false, makeNoLongerReadable: true);
                return dst;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASTC Encoder] Readback Cubemap compression failed: {ex.Message}");
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(dst);
                else
                    UnityEngine.Object.DestroyImmediate(dst);
                return null;
            }
        }

        internal static Texture2DArray CompressTexture2DArray(
            Texture2DArray source,
            TextureType textureType,
            BlockSize blockSize,
            CompressQuality quality,
            bool includeMipmaps,
            bool useAlphaWeight)
        {
            if (source == null || !IsSupported)
                return null;

            GraphicsFormat targetFormat = Encoder.GetTargetAstcFormat(source.graphicsFormat, blockSize, textureType);
            if (!Encoder.CanCreateAstcTexture(targetFormat))
            {
                Debug.LogError($"ASTC texture format '{targetFormat}' is not supported on this device.");
                return null;
            }

            int mipCount = includeMipmaps ? Math.Max(1, source.mipmapCount) : 1;
            TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var dst = new Texture2DArray(source.width, source.height, source.depth, targetFormat, flags, mipCount)
            {
                name = source.name
            };

            try
            {
                for (int slice = 0; slice < source.depth; slice++)
                {
                    using ReadbackSnapshot snap = CaptureSlice(source, slice, TextureDimension.Tex2DArray, textureType, includeMipmaps, generateMipmaps: false);
                    if (snap == null)
                        throw new InvalidOperationException($"Failed to read back Texture2DArray slice {slice}.");

                    using AstcRaw raw = AstcCpuBackend.CompressSnapshotSync(
                        snap.Buffer, snap.ByteLength, snap.Width, snap.Height, snap.MipCount,
                        snap.Format, textureType, blockSize, quality, 1, useAlphaWeight, false, 1u, false);
                    if (raw == null)
                        throw new InvalidOperationException($"Failed to encode Texture2DArray slice {slice}.");

                    UploadMipSlices(raw, source.width, source.height, snap.MipCount, blockSize, (sliceBytes, mip) =>
                    {
                        dst.SetPixelData(sliceBytes, mip, slice);
                    });
                }

                dst.Apply(false, makeNoLongerReadable: true);
                return dst;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASTC Encoder] Readback Texture2DArray compression failed: {ex.Message}");
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(dst);
                else
                    UnityEngine.Object.DestroyImmediate(dst);
                return null;
            }
        }

        private static Texture2D CreateAstcTextureFromRaw(AstcRaw raw, int width, int height, GraphicsFormat targetFormat, BlockSize blockSize, string name)
        {
            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, width, height, blockSize);
            TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
            var tex = new Texture2D(width, height, targetFormat, mipCount, flags)
            {
                name = name
            };
            tex.LoadRawTextureData(raw.Data, raw.Length);
            tex.Apply(false, makeNoLongerReadable: true);
            AstcAtlasShadowUpdater.Register(tex, raw, blockSize);
            return tex;
        }

        private static unsafe void UploadMipSlices(AstcRaw raw, int width, int height, int mipCount, BlockSize blockSize, Action<byte[], int> uploadMip)
        {
            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return;

            int offset = 0;
            byte* basePtr = (byte*)raw.Data;
            for (int mip = 0; mip < mipCount; mip++)
            {
                int mw = Math.Max(1, width >> mip);
                int mh = Math.Max(1, height >> mip);
                long blocksX = (mw + (long)bx - 1L) / (long)bx;
                long blocksY = (mh + (long)by - 1L) / (long)by;
                int mipBytes = (int)(blocksX * blocksY * 16L);
                if (offset + mipBytes > raw.Length)
                    throw new InvalidOperationException("ASTC raw buffer shorter than expected mip chain.");

                byte[] slice = new byte[mipBytes];
                Marshal.Copy((IntPtr)(basePtr + offset), slice, 0, mipBytes);
                uploadMip(slice, mip);
                offset += mipBytes;
            }
        }

        private static int ComputeMaxMipCount(int width, int height)
        {
            int maxDim = Math.Max(width, height);
            int count = 1;
            while (maxDim > 1)
            {
                maxDim >>= 1;
                count++;
            }
            return count;
        }
    }
}
