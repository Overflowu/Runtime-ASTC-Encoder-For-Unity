using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Aperture.Astcenc.Runtime.NativeAPI;

namespace Aperture.Astcenc.Runtime
{
    internal static class ImageExtensions
    {
        private static astcenc_type GetDataType(int bitness)
        {
            if (bitness == 8)
            {
                return astcenc_type.ASTCENC_TYPE_U8;
            }
            else if (bitness == 16)
            {
                return astcenc_type.ASTCENC_TYPE_F16;
            }
            else// if (bitness == 32)
            {
                return astcenc_type.ASTCENC_TYPE_F32;
            }
        }

        /// <summary>
        /// Zero-copy binding for 4-channel RGBA formats whose native memory layout already matches astcenc_image.
        /// Caller must ensure rawData remains valid until compression completes and pass ownsSliceMemory: false to Release().
        /// </summary>
        internal static unsafe void LoadFromDirect<T>(this ref astcenc_image image, NativeSlice<T> rawData, int width, int height, int bitness) where T : unmanaged
        {
            image.LoadFromDirectPtr(rawData.GetUnsafeReadOnlyPtr(), width, height, bitness);
        }

        /// <summary>
        /// Zero-copy binding from an existing unmanaged pointer whose memory layout already matches 4-channel astcenc_image.
        /// Caller must ensure slicePtr remains valid until compression completes and pass ownsSliceMemory: false to Release().
        /// </summary>
        internal static unsafe void LoadFromDirectPtr(this ref astcenc_image image, void* slicePtr, int width, int height, int bitness)
        {
            if (image.data != IntPtr.Zero)
            {
                throw new InvalidOperationException("astcenc_image already holds allocated data; call Release() before rebinding.");
            }

            IntPtr dataPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(dataPtr, 0, (IntPtr)slicePtr);
            }
            catch
            {
                Marshal.FreeHGlobal(dataPtr);
                throw;
            }

            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = dataPtr;
        }

        /// <summary>
        /// Expands a contiguous unmanaged source buffer of packed pixels into an owned 4-channel astcenc_image.
        /// </summary>
        internal static unsafe void LoadFromPtr<T>(this ref astcenc_image image, T* srcPtr, int width, int height, int bitness) where T : unmanaged, IRawData
        {
            if (image.data != IntPtr.Zero)
            {
                image.Release();
            }

            int pixelCount = width * height;
            int stride = (bitness / 8) * 4;
            long byteCount = (long)pixelCount * stride;
            IntPtr slicePtr = Marshal.AllocHGlobal((IntPtr)byteCount);
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                byte* dst = (byte*)slicePtr;
                for (int i = 0; i < pixelCount; i++)
                {
                    srcPtr[i].WriteTo(dst);
                    dst += stride;
                }

                dataPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(dataPtr, 0, slicePtr);
            }
            catch
            {
                if (dataPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                Marshal.FreeHGlobal(slicePtr);
                throw;
            }

            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = dataPtr;
        }

        internal static void LoadFrom<T>(this ref astcenc_image image, NativeSlice<T> rawData, int width, int height, int bitness) where T : unmanaged, IRawData
        {
            if (image.data != IntPtr.Zero)
            {
                image.Release();
            }

            IntPtr slicePtr = LoadData(rawData, bitness);
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                dataPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(dataPtr, 0, slicePtr);
            }
            catch
            {
                if (dataPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                Marshal.FreeHGlobal(slicePtr);
                throw;
            }

            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = dataPtr;
        }

        /// <summary>
        /// Allocates an owned 4-channel output image buffer for astcenc_decompress_image.
        /// </summary>
        internal static void AllocateOutput(this ref astcenc_image image, int width, int height, int bitness, out IntPtr slicePtr, out int byteCount)
        {
            if (image.data != IntPtr.Zero)
            {
                image.Release();
            }

            int stride = (bitness / 8) * 4;
            long totalBytes = (long)width * height * stride;
            if (totalBytes <= 0 || totalBytes > int.MaxValue)
            {
                throw new OverflowException($"Decompressed image buffer size out of range: {width}x{height} ({bitness}-bit).");
            }

            byteCount = (int)totalBytes;
            slicePtr = Marshal.AllocHGlobal((IntPtr)totalBytes);
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                dataPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(dataPtr, 0, slicePtr);
            }
            catch
            {
                if (dataPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                Marshal.FreeHGlobal(slicePtr);
                slicePtr = IntPtr.Zero;
                byteCount = 0;
                throw;
            }

            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = dataPtr;
        }

        internal static void Release(this ref astcenc_image image, bool ownsSliceMemory = true)
        {
            if (image.data != IntPtr.Zero)
            {
                if (ownsSliceMemory)
                {
                    for (int i = 0; i < image.dim_z; i++)
                    {
                        IntPtr slicePtr = Marshal.ReadIntPtr(image.data, i * IntPtr.Size);
                        if (slicePtr != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(slicePtr);
                        }
                    }
                }
                Marshal.FreeHGlobal(image.data);
            }
            image.data = IntPtr.Zero;
        }

        private static IntPtr LoadData<T>(NativeSlice<T> raw, int bitness) where T : unmanaged, IRawData
        {
            int stride = (bitness / 8) * 4;
            long byteCount = (long)raw.Length * stride;
            IntPtr dataPtr = Marshal.AllocHGlobal((IntPtr)byteCount);
            try
            {
                Copy(raw, dataPtr, stride);
                return dataPtr;
            }
            catch
            {
                Marshal.FreeHGlobal(dataPtr);
                throw;
            }
        }

        private static unsafe void Copy<T>(NativeSlice<T> raw, IntPtr destPtr, int stride) where T : unmanaged, IRawData
        {
            int length = raw.Length;
            byte* dst = (byte*)destPtr;
            if (raw.Stride == sizeof(T))
            {
                T* srcPtr = (T*)raw.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < length; i++)
                {
                    srcPtr[i].WriteTo(dst);
                    dst += stride;
                }
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    T pixel = raw[i];
                    pixel.WriteTo(dst);
                    dst += stride;
                }
            }
        }
    }
}
