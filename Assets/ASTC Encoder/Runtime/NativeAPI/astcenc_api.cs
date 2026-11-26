using System;
using System.Runtime.InteropServices;

namespace Aperture.Astcenc.Runtime.NativeAPI
{
public static class astcenc_api
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string NeonLibraryName = "libastcenc-neon-shared";
    private const string NoneLibraryName = "libastcenc-none-shared";

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_config_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_config_init_neon(
        astcenc_profile profile,
        uint block_x,
        uint block_y,
        uint block_z,
        float quality,
        uint flags,
        ref astcenc_config config);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_config_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_config_init_none(
        astcenc_profile profile,
        uint block_x,
        uint block_y,
        uint block_z,
        float quality,
        uint flags,
        ref astcenc_config config);

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_context_alloc", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_context_alloc_neon(
        ref astcenc_config config,
        uint thread_count,
        ref IntPtr context,
        IntPtr parent_context);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_context_alloc", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_context_alloc_none(
        ref astcenc_config config,
        uint thread_count,
        ref IntPtr context,
        IntPtr parent_context);

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_compress_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_image_neon(
        IntPtr context,
        ref astcenc_image image,
        ref astcenc_swizzle swizzle,
        IntPtr data_out,
        UIntPtr data_len,
        uint thread_index);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_compress_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_image_none(
        IntPtr context,
        ref astcenc_image image,
        ref astcenc_swizzle swizzle,
        IntPtr data_out,
        UIntPtr data_len,
        uint thread_index);

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_compress_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_reset_neon(
        IntPtr context);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_compress_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_reset_none(
        IntPtr context);

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_compress_cancel", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_cancel_neon(
        IntPtr context);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_compress_cancel", CallingConvention = CallingConvention.Cdecl)]
    private static extern astcenc_error astcenc_compress_cancel_none(
        IntPtr context);

    [DllImport(NeonLibraryName, EntryPoint = "astcenc_context_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void astcenc_context_free_neon(
        IntPtr context);

    [DllImport(NoneLibraryName, EntryPoint = "astcenc_context_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void astcenc_context_free_none(
        IntPtr context);

    /**
     * Populate a codec config based on default settings.
     *
     * Power users can edit the returned config struct to fine tune before allocating the context.
     *
     * @param      profile   Color profile.
     * @param      block_x   ASTC block size X dimension.
     * @param      block_y   ASTC block size Y dimension.
     * @param      block_z   ASTC block size Z dimension.
     * @param      quality   Search quality preset / effort level. Either an
     *                       @c ASTCENC_PRE_* value, or a effort level between 0
     *                       and 100. Performance is not linear between 0 and 100.

     * @param      flags     A valid set of @c ASTCENC_FLG_* flag bits.
     * @param[out] config    Output config struct to populate.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if the inputs are invalid
     * either individually, or in combination.
     */
    public static astcenc_error astcenc_config_init(
        astcenc_profile profile,
        uint block_x,
        uint block_y,
        uint block_z,
        float quality,
        uint flags,
        ref astcenc_config config)
    {
        return IntPtr.Size == 8
            ? astcenc_config_init_neon(profile, block_x, block_y, block_z, quality, flags, ref config)
            : astcenc_config_init_none(profile, block_x, block_y, block_z, quality, flags, ref config);
    }

    /**
     * @brief Allocate a new codec context based on a config.
     *
     * This function allocates all of the memory resources and threads needed by the codec. This can be
     * slow, so it is recommended that contexts are reused to serially compress or decompress multiple
     * images to amortize setup cost.
     *
     * Contexts can be allocated to support only decompression using the @c ASTCENC_FLG_DECOMPRESS_ONLY
     * flag when creating the configuration. The compression functions will fail if invoked. For a
     * decompress-only library build the @c ASTCENC_FLG_DECOMPRESS_ONLY flag must be set when creating
     * any context.
     *
     * @param[in]  config           Codec config.
     * @param      thread_count     Thread count to configure for.
     * @param[out] context          Location to store an opaque context pointer.
     * @param[in]  parent_context   Optional parent context from which to inherit read-only data tables, or IntPtr.Zero.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if context creation failed.
     */
    public static astcenc_error astcenc_context_alloc(
        ref astcenc_config config,
        uint thread_count,
        ref IntPtr context,
        IntPtr parent_context)
    {
        return IntPtr.Size == 8
            ? astcenc_context_alloc_neon(ref config, thread_count, ref context, parent_context)
            : astcenc_context_alloc_none(ref config, thread_count, ref context, parent_context);
    }

    /**
     * @brief Compress an image.
     *
     * A single context can only compress or decompress a single image at a time.
     *
     * For a context configured for multi-threading, any set of the N threads can call this function.
     * Work will be dynamically scheduled across the threads available. Each thread must have a unique
     * @c thread_index.
     *
     * @param         context        Codec context.
     * @param[in,out] image          An input image, in 2D slices.
     * @param         swizzle        Compression data swizzle, applied before compression.
     * @param[out]    data_out       Pointer to output data array.
     * @param         data_len       Length of the output data array.
     * @param         thread_index   Thread index [0..N-1] of calling thread.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if compression failed.
     */
    public static astcenc_error astcenc_compress_image(
        IntPtr context,
        ref astcenc_image image,
        ref astcenc_swizzle swizzle,
        IntPtr data_out,
        UIntPtr data_len,
        uint thread_index)
    {
        return IntPtr.Size == 8
            ? astcenc_compress_image_neon(context, ref image, ref swizzle, data_out, data_len, thread_index)
            : astcenc_compress_image_none(context, ref image, ref swizzle, data_out, data_len, thread_index);
    }

    /**
     * @brief Reset the codec state for a new compression.
     *
     * The caller is responsible for synchronizing threads in the worker thread pool. This function must
     * only be called when all threads have exited the @c astcenc_compress_image() function for image N,
     * but before any thread enters it for image N + 1.
     *
     * Calling this is not required (but won't hurt), if the context is created for single threaded use.
     *
     * @param context   Codec context.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if reset failed.
     */
    public static astcenc_error astcenc_compress_reset(
        IntPtr context)
    {
        return IntPtr.Size == 8
            ? astcenc_compress_reset_neon(context)
            : astcenc_compress_reset_none(context);
    }

    /**
     * @brief Cancel any pending compression operation.
     *
     * @param context   Codec context.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if cancellation failed.
     */
    public static astcenc_error astcenc_compress_cancel(
        IntPtr context)
    {
        return IntPtr.Size == 8
            ? astcenc_compress_cancel_neon(context)
            : astcenc_compress_cancel_none(context);
    }

    /**
     * Free the compressor context.
     *
     * @param context   The codec context.
     */
    public static void astcenc_context_free(
        IntPtr context)
    {
        if (IntPtr.Size == 8)
            astcenc_context_free_neon(context);
        else
            astcenc_context_free_none(context);
    }
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_WIN || (!UNITY_EDITOR && (UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_STANDALONE_WIN))
#if UNITY_EDITOR_OSX
    internal const string LibraryName = "libastcenc-shared";
#elif UNITY_EDITOR_WIN
    internal const string LibraryName = "astcenc-sse4.1-shared.dll";
#elif UNITY_IOS
    internal const string LibraryName = "__Internal";
#elif UNITY_STANDALONE_OSX
    internal const string LibraryName = "libastcenc-shared";
#elif UNITY_STANDALONE_WIN
    internal const string LibraryName = "astcenc-sse4.1-shared.dll";
#endif

    /**
     * Populate a codec config based on default settings.
     *
     * Power users can edit the returned config struct to fine tune before allocating the context.
     *
     * @param      profile   Color profile.
     * @param      block_x   ASTC block size X dimension.
     * @param      block_y   ASTC block size Y dimension.
     * @param      block_z   ASTC block size Z dimension.
     * @param      quality   Search quality preset / effort level. Either an
     *                       @c ASTCENC_PRE_* value, or a effort level between 0
     *                       and 100. Performance is not linear between 0 and 100.

     * @param      flags     A valid set of @c ASTCENC_FLG_* flag bits.
     * @param[out] config    Output config struct to populate.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if the inputs are invalid
     * either individually, or in combination.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_config_init", CallingConvention = CallingConvention.Cdecl)]
    public static extern astcenc_error astcenc_config_init(
        astcenc_profile profile, 
        uint block_x, 
        uint block_y, 
        uint block_z, 
        float quality, 
        uint flags, 
        ref astcenc_config config);

    /**
     * @brief Allocate a new codec context based on a config.
     *
     * This function allocates all of the memory resources and threads needed by the codec. This can be
     * slow, so it is recommended that contexts are reused to serially compress or decompress multiple
     * images to amortize setup cost.
     *
     * Contexts can be allocated to support only decompression using the @c ASTCENC_FLG_DECOMPRESS_ONLY
     * flag when creating the configuration. The compression functions will fail if invoked. For a
     * decompress-only library build the @c ASTCENC_FLG_DECOMPRESS_ONLY flag must be set when creating
     * any context.
     *
     * @param[in]  config           Codec config.
     * @param      thread_count     Thread count to configure for.
     * @param[out] context          Location to store an opaque context pointer.
     * @param[in]  parent_context   Optional parent context from which to inherit read-only data tables, or IntPtr.Zero.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if context creation failed.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_context_alloc", CallingConvention = CallingConvention.Cdecl)]
    public static extern astcenc_error astcenc_context_alloc(
        ref astcenc_config config, 
        uint thread_count, 
        ref IntPtr context,
        IntPtr parent_context);

    /**
     * @brief Compress an image.
     *
     * A single context can only compress or decompress a single image at a time.
     *
     * For a context configured for multi-threading, any set of the N threads can call this function.
     * Work will be dynamically scheduled across the threads available. Each thread must have a unique
     * @c thread_index.
     *
     * @param         context        Codec context.
     * @param[in,out] image          An input image, in 2D slices.
     * @param         swizzle        Compression data swizzle, applied before compression.
     * @param[out]    data_out       Pointer to output data array.
     * @param         data_len       Length of the output data array.
     * @param         thread_index   Thread index [0..N-1] of calling thread.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if compression failed.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_compress_image", CallingConvention = CallingConvention.Cdecl)]
    public static extern astcenc_error astcenc_compress_image(
        IntPtr context,
        ref astcenc_image image,
        ref astcenc_swizzle swizzle,
        IntPtr data_out,
        UIntPtr data_len,
        uint thread_index);
    /**
     * @brief Reset the codec state for a new compression.
     *
     * The caller is responsible for synchronizing threads in the worker thread pool. This function must
     * only be called when all threads have exited the @c astcenc_compress_image() function for image N,
     * but before any thread enters it for image N + 1.
     *
     * Calling this is not required (but won't hurt), if the context is created for single threaded use.
     *
     * @param context   Codec context.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if reset failed.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_compress_reset", CallingConvention = CallingConvention.Cdecl)]
    public static extern astcenc_error astcenc_compress_reset(
        IntPtr context);

    /**
     * @brief Cancel any pending compression operation.
     *
     * @param context   Codec context.
     *
     * @return @c ASTCENC_SUCCESS on success, or an error if cancellation failed.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_compress_cancel", CallingConvention = CallingConvention.Cdecl)]
    public static extern astcenc_error astcenc_compress_cancel(
        IntPtr context);

    /**
     * Free the compressor context.
     *
     * @param context   The codec context.
     */
    [DllImport(LibraryName, EntryPoint = "astcenc_context_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void astcenc_context_free(
        IntPtr context);
#else
    public static astcenc_error astcenc_config_init(
        astcenc_profile profile,
        uint block_x,
        uint block_y,
        uint block_z,
        float quality,
        uint flags,
        ref astcenc_config config) => astcenc_error.ASTCENC_ERR_NOT_IMPLEMENTED;

    public static astcenc_error astcenc_context_alloc(
        ref astcenc_config config,
        uint thread_count,
        ref IntPtr context,
        IntPtr parent_context) => astcenc_error.ASTCENC_ERR_NOT_IMPLEMENTED;

    public static astcenc_error astcenc_compress_image(
        IntPtr context,
        ref astcenc_image image,
        ref astcenc_swizzle swizzle,
        IntPtr data_out,
        UIntPtr data_len,
        uint thread_index) => astcenc_error.ASTCENC_ERR_NOT_IMPLEMENTED;

    public static astcenc_error astcenc_compress_reset(
        IntPtr context) => astcenc_error.ASTCENC_ERR_NOT_IMPLEMENTED;

    public static astcenc_error astcenc_compress_cancel(
        IntPtr context) => astcenc_error.ASTCENC_ERR_NOT_IMPLEMENTED;

    public static void astcenc_context_free(
        IntPtr context) { }
#endif
}
}
