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
            if (image.data != IntPtr.Zero)
            {
                throw new InvalidOperationException("astcenc_image already holds allocated data; call Release() before rebinding.");
            }

            IntPtr slicePtr = (IntPtr)rawData.GetUnsafeReadOnlyPtr();
            IntPtr dataPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(dataPtr, 0, slicePtr);
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
        /// Bulk memcpy into owned unmanaged memory for 4-channel RGBA formats (e.g., for background async compression).
        /// </summary>
        internal static unsafe void LoadFromCopy<T>(this ref astcenc_image image, NativeSlice<T> rawData, int width, int height, int bitness) where T : unmanaged
        {
            if (image.data != IntPtr.Zero)
            {
                image.Release();
            }

            int elemSize = sizeof(T);
            long byteCount = (long)rawData.Length * elemSize;
            IntPtr slicePtr = Marshal.AllocHGlobal((IntPtr)byteCount);
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                if (rawData.Stride == elemSize)
                {
                    UnsafeUtility.MemCpy((void*)slicePtr, rawData.GetUnsafeReadOnlyPtr(), byteCount);
                }
                else
                {
                    T* dst = (T*)slicePtr;
                    for (int i = 0; i < rawData.Length; i++)
                    {
                        dst[i] = rawData[i];
                    }
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
            long baseAddr = destPtr.ToInt64();
            if (raw.Stride == sizeof(T))
            {
                T* srcPtr = (T*)raw.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < length; i++)
                {
                    srcPtr[i].WriteTo(new IntPtr(baseAddr + (long)i * stride));
                }
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    T pixel = raw[i];
                    pixel.WriteTo(new IntPtr(baseAddr + (long)i * stride));
                }
            }
        }
    }
}
