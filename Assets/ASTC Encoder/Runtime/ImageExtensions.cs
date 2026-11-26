using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Aperture.Astcenc.Runtime
{
    public static class ImageExtensions
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
            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = Marshal.AllocHGlobal(IntPtr.Size);

            IntPtr slicePtr = (IntPtr)rawData.GetUnsafeReadOnlyPtr();
            Marshal.WriteIntPtr(image.data, 0, slicePtr);
        }

        /// <summary>
        /// Bulk memcpy into owned unmanaged memory for 4-channel RGBA formats (e.g., for background async compression).
        /// </summary>
        internal static unsafe void LoadFromCopy<T>(this ref astcenc_image image, NativeSlice<T> rawData, int width, int height, int bitness) where T : unmanaged
        {
            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = Marshal.AllocHGlobal(IntPtr.Size);

            long byteCount = (long)rawData.Length * sizeof(T);
            IntPtr slicePtr = Marshal.AllocHGlobal((IntPtr)byteCount);
            UnsafeUtility.MemCpy((void*)slicePtr, rawData.GetUnsafeReadOnlyPtr(), byteCount);
            Marshal.WriteIntPtr(image.data, 0, slicePtr);
        }

        internal static void LoadFrom<T>(this ref astcenc_image image, NativeSlice<T> rawData, int width, int height, int bitness) where T : unmanaged, IRawData
        {
            if (image.data != IntPtr.Zero)
            {
                image.Release();
            }
            image.dim_x = (uint)width;
            image.dim_y = (uint)height;
            image.dim_z = 1;
            image.data_type = GetDataType(bitness);
            image.data = Marshal.AllocHGlobal(IntPtr.Size);

            IntPtr slicePtr = LoadData(rawData, bitness);
            Marshal.WriteIntPtr(image.data, 0, slicePtr);
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
            int bytes = bitness / 8;
            IntPtr dataPtr = Marshal.AllocHGlobal(raw.Length * bytes * 4);
            Copy(raw, dataPtr, bytes * 4);

            return dataPtr;
        }

        private static unsafe void Copy<T>(NativeSlice<T> raw, IntPtr destPtr, int stride) where T : unmanaged, IRawData
        {
            int length = raw.Length;
            if (raw.Stride == sizeof(T))
            {
                T* srcPtr = (T*)raw.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < length; i++)
                {
                    srcPtr[i].WriteTo(destPtr + i * stride);
                }
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    T pixel = raw[i];
                    pixel.WriteTo(destPtr + i * stride);
                }
            }
        }
    }
}
