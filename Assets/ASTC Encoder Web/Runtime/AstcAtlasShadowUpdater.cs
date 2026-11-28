using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// CPU shadow-buffer emulation for <see cref="Encoder.Encode(CommandBuffer, Texture, Texture, ...)"/>
    /// on platforms without Compute Shader / CopyTexture-from-UAV support (such as WebGL2).
    /// </summary>
    internal static class AstcAtlasShadowUpdater
    {
        private sealed class ShadowStorage
        {
            public readonly Dictionary<(int slice, int mip), byte[]> Mips = new Dictionary<(int slice, int mip), byte[]>();
            public bool WarnedZeroInit;
        }

        private static readonly ConditionalWeakTable<Texture, ShadowStorage> s_Shadows = new ConditionalWeakTable<Texture, ShadowStorage>();

        internal static unsafe void Register(Texture2D texture, AstcRaw raw, BlockSize blockSize)
        {
            if (texture == null || raw == null || raw.Data == IntPtr.Zero || raw.Length <= 0)
                return;

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
                return;

            int mipCount = AstcKtxContainer.InferMipCount(raw.Length, texture.width, texture.height, blockSize);
            ShadowStorage storage = s_Shadows.GetOrCreateValue(texture);
            byte* basePtr = (byte*)raw.Data;
            int offset = 0;

            for (int mip = 0; mip < mipCount; mip++)
            {
                int mw = Math.Max(1, texture.width >> mip);
                int mh = Math.Max(1, texture.height >> mip);
                long blocksX = (mw + (long)bx - 1L) / (long)bx;
                long blocksY = (mh + (long)by - 1L) / (long)by;
                int mipBytes = (int)(blocksX * blocksY * 16L);
                if (offset + mipBytes > raw.Length)
                    break;

                byte[] copy = new byte[mipBytes];
                Marshal.Copy((IntPtr)(basePtr + offset), copy, 0, mipBytes);
                storage.Mips[(0, mip)] = copy;
                offset += mipBytes;
            }
        }

        internal static unsafe void Encode(
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
            int destSlice)
        {
            if (source == null || destination == null)
            {
                Debug.LogError("[ASTC Encoder] Source or destination texture is null.");
                return;
            }

            if (!AstcReadbackBridge.IsSupported)
            {
                Debug.LogError("[ASTC Encoder] Neither GPU Compute nor CPU readback fallback is available on this platform.");
                return;
            }

            if (!Encoder.GetBlockSize(blockSize, out uint bx, out uint by, out _))
            {
                Debug.LogError("[ASTC Encoder] Unsupported ASTC block size: " + blockSize);
                return;
            }

            if (destX < 0 || destY < 0 || (destX % (int)bx) != 0 || (destY % (int)by) != 0)
            {
                Debug.LogError($"[ASTC Encoder] Atlas destination offset ({destX}, {destY}) must be non-negative and aligned to block size {bx}x{by}.");
                return;
            }

            int srcWidth = Math.Max(1, source.width >> sourceMip);
            int srcHeight = Math.Max(1, source.height >> sourceMip);
            int dstMipW = Math.Max(1, destination.width >> destMip);
            int dstMipH = Math.Max(1, destination.height >> destMip);
            if (destX + srcWidth > dstMipW || destY + srcHeight > dstMipH)
            {
                Debug.LogError($"[ASTC Encoder] Tile ({srcWidth}x{srcHeight}) at ({destX},{destY}) exceeds destination mip {destMip} ({dstMipW}x{dstMipH}).");
                return;
            }

            // Capture only the requested sourceMip (by temporarily reading mip 0 or blitting at _SourceMip).
            // CaptureSlice captures from mip 0..mipCount-1 when includeMipmaps=true, so for sourceMip > 0 we capture 1+sourceMip mips or add a single-mip capture.
            // Using CaptureSingleMip keeps readback minimal.
            using AstcReadbackBridge.ReadbackSnapshot snap = CaptureSingleMip(source, sourceSlice, sourceMip, textureType);
            if (snap == null)
            {
                Debug.LogError("[ASTC Encoder] Failed to read back source texture for atlas tile update.");
                return;
            }

            using AstcRaw tileRaw = AstcCpuBackend.CompressSnapshotSync(
                snap.Buffer, snap.ByteLength, snap.Width, snap.Height, 1,
                snap.Format, textureType, blockSize, quality, 1, useAlphaWeight, false, 1u, false);
            if (tileRaw == null)
            {
                Debug.LogError("[ASTC Encoder] Failed to encode atlas tile on CPU.");
                return;
            }

            long dstBlocksX = (dstMipW + (long)bx - 1L) / (long)bx;
            long dstBlocksY = (dstMipH + (long)by - 1L) / (long)by;
            int dstMipBytes = (int)(dstBlocksX * dstBlocksY * 16L);

            ShadowStorage storage = s_Shadows.GetOrCreateValue(destination);
            if (!storage.Mips.TryGetValue((destSlice, destMip), out byte[] dstShadow) || dstShadow.Length != dstMipBytes)
            {
                dstShadow = new byte[dstMipBytes];
                if (destination is Texture2D dstTex2d && dstTex2d.isReadable)
                {
                    NativeArray<byte> existing = dstTex2d.GetRawTextureData<byte>(destMip);
                    if (existing.Length == dstMipBytes)
                    {
                        existing.CopyTo(dstShadow);
                    }
                }
                else if (!storage.WarnedZeroInit)
                {
                    storage.WarnedZeroInit = true;
                    Debug.LogWarning("[ASTC Encoder] WebGL atlas update: destination texture has no readable/registered shadow buffer; uninitialized blocks in this mip will be zeroed.");
                }
                storage.Mips[(destSlice, destMip)] = dstShadow;
            }

            int srcBlocksX = (int)((srcWidth + (long)bx - 1L) / (long)bx);
            int srcBlocksY = (int)((srcHeight + (long)by - 1L) / (long)by);
            int dstBlockOriginX = destX / (int)bx;
            int dstBlockOriginY = destY / (int)by;

            byte* tilePtr = (byte*)tileRaw.Data;
            int rowBytes = srcBlocksX * 16;
            for (int byRow = 0; byRow < srcBlocksY; byRow++)
            {
                int srcOff = byRow * rowBytes;
                int dstOff = ((dstBlockOriginY + byRow) * (int)dstBlocksX + dstBlockOriginX) * 16;
                if (srcOff + rowBytes <= tileRaw.Length && dstOff + rowBytes <= dstShadow.Length)
                {
                    Marshal.Copy((IntPtr)(tilePtr + srcOff), dstShadow, dstOff, rowBytes);
                }
            }

            if (destination is Texture2D tex2d)
            {
                tex2d.SetPixelData(dstShadow, destMip);
                tex2d.Apply(false, makeNoLongerReadable: false);
            }
            else if (destination is Cubemap cube)
            {
                cube.SetPixelData(dstShadow, destMip, (CubemapFace)destSlice);
                cube.Apply(false, makeNoLongerReadable: false);
            }
            else if (destination is Texture2DArray arr)
            {
                arr.SetPixelData(dstShadow, destMip, destSlice);
                arr.Apply(false, makeNoLongerReadable: false);
            }
        }

        private static AstcReadbackBridge.ReadbackSnapshot CaptureSingleMip(Texture src, int slice, int sourceMip, TextureType textureType)
        {
            if (sourceMip <= 0)
            {
                return AstcReadbackBridge.CaptureSlice(src, slice, src.dimension, textureType, includeMipmaps: false, generateMipmaps: false);
            }

            // Capture 0..sourceMip and slice out the last mip? No, Blit at _SourceMip directly would be cleaner.
            // Reuse CaptureSlice with mipCount = sourceMip + 1 and copy only the tail mip if needed,
            // or add a direct single-mip helper. Let's extract the tail mip here cleanly.
            using AstcReadbackBridge.ReadbackSnapshot chain = AstcReadbackBridge.CaptureSlice(
                src, slice, src.dimension, textureType, includeMipmaps: true, generateMipmaps: false);
            if (chain == null)
                return null;

            int effectiveMip = Math.Min(sourceMip, chain.MipCount - 1);
            int bpp = chain.Format == UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat ? 8 : 4;
            long offset = 0;
            for (int m = 0; m < effectiveMip; m++)
            {
                offset += (long)Math.Max(1, chain.Width >> m) * Math.Max(1, chain.Height >> m) * bpp;
            }
            int mw = Math.Max(1, chain.Width >> effectiveMip);
            int mh = Math.Max(1, chain.Height >> effectiveMip);
            int mipBytes = mw * mh * bpp;
            if (offset + mipBytes > chain.ByteLength)
                return null;

            IntPtr single = Marshal.AllocHGlobal(mipBytes);
            unsafe
            {
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy((void*)single, (byte*)chain.Buffer + offset, mipBytes);
            }
            return new AstcReadbackBridge.ReadbackSnapshot
            {
                Buffer = single,
                ByteLength = mipBytes,
                Format = chain.Format,
                Width = mw,
                Height = mh,
                MipCount = 1
            };
        }
    }
}
