using System;
using System.Runtime.InteropServices;

namespace Aperture.Astcenc.Runtime
{
    internal static class AstcWebNativeApi
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int astcenc_wasm_mt_available();

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int astcenc_wasm_mt_begin_compress(
            IntPtr context,
            uint thread_count,
            uint block_x,
            uint block_y,
            int swz_r,
            int swz_g,
            int swz_b,
            int swz_a,
            uint width,
            uint height,
            uint mip_count,
            int astc_type_int,
            IntPtr pixels_in,
            UIntPtr pixels_in_len,
            UIntPtr expected_out_len);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int astcenc_wasm_mt_begin_decompress(
            IntPtr context,
            uint thread_count,
            uint width,
            uint height,
            int astc_type_int,
            IntPtr astc_in,
            UIntPtr astc_in_len,
            UIntPtr expected_out_len);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int astcenc_wasm_mt_poll(int handle, out IntPtr out_ptr, out UIntPtr out_len);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void astcenc_wasm_mt_finish(int handle);

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void astcenc_wasm_mt_cancel(int handle);
#endif
    }
}
